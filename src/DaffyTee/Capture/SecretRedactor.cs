using System.Buffers;
using System.Text;
using System.Text.RegularExpressions;
using DaffyTee.Commands;
using DaffyTee.Tds;

namespace DaffyTee.Capture;

/// <summary>
/// Masks passwords and other secrets written into SQL - CREATE LOGIN ... WITH PASSWORD = '...',
/// CREATE CREDENTIAL ... SECRET = '...', sp_addlinkedsrvlogin, ENCRYPTBYPASSPHRASE, a password in a
/// connection string handed to OPENDATASOURCE - before an event reaches any output: the sink, the
/// SQL log, :watch and :recent. On by default; --no-redact turns it off.
///
/// It runs on the pipeline's worker, never on the relay, and costs ordinary SQL one vectorised
/// search for a handful of words. Only text containing one of them is tokenised and walked. What
/// it masks is a value standing in a secret's place: after PASSWORD =, SECRET =, KEY_SOURCE = and
/// any name containing password, pwd, secret or passphrase followed by =; in the password argument
/// of the procedures and functions that take one; inside WITH PRIVATE KEY (...); and in variables
/// that carry a secret into one of those places (DECLARE @p ... = 'x'; ... PASSWORD = @p). SQL inside
/// a string - EXEC('...'), sp_executesql N'...' - is walked the same way. What it cannot recognise
/// is free text: a password in a comment, or written as ordinary data (INSERT ... VALUES).
/// </summary>
internal static partial class SecretRedactor
{
    public const string Mask = "***";

    /// <summary>Text without any of these contains nothing this class would mask.</summary>
    private static readonly SearchValues<string> Hints = SearchValues.Create(
        ["password", "passwd", "pwd", "secret", "passphrase", "key_source", "decryptby", "signby", "private",
         "openrowset", "sp_addlogin", "sp_addlinkedsrvlogin", "approle", "sp_change_users_login", CommandParser.Schema],
        StringComparison.OrdinalIgnoreCase);

    /// <summary>A name containing one of these, followed by =, is followed by a secret.</summary>
    private static readonly SearchValues<string> SecretNames = SearchValues.Create(
        ["password", "passwd", "pwd", "secret", "passphrase", "key_source"], StringComparison.OrdinalIgnoreCase);

    /// <summary>Functions that take a secret, and which argument (from 1) it is.</summary>
    private static readonly Dictionary<string, int> SecretArguments = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ENCRYPTBYPASSPHRASE"] = 1, ["DECRYPTBYPASSPHRASE"] = 1, ["PWDCOMPARE"] = 1, ["PWDENCRYPT"] = 1,
        ["DECRYPTBYKEYAUTOASYMKEY"] = 2, ["DECRYPTBYKEYAUTOCERT"] = 2,
        ["DECRYPTBYASYMKEY"] = 3, ["DECRYPTBYCERT"] = 3, ["SIGNBYASYMKEY"] = 3, ["SIGNBYCERT"] = 3,
    };

    /// <summary>
    /// System procedures that take a password, by position (from 1) and by name. Most of the names
    /// would be caught as secret names anyway; sp_password's @old and @new would not.
    /// </summary>
    private static readonly Dictionary<string, (int[] Positions, string[] Names)> SecretParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        ["sp_addlogin"] = ([2], ["@passwd"]),
        ["sp_password"] = ([1, 2], ["@old", "@new"]),
        ["sp_addlinkedsrvlogin"] = ([5], ["@rmtpassword"]),
        ["sp_setapprole"] = ([2], ["@password"]),
        ["sp_addapprole"] = ([2], ["@password"]),
        ["sp_approlepassword"] = ([2], ["@newpwd"]),
        ["sp_control_dbmasterkey_password"] = ([2], ["@password"]),
        ["sp_change_users_login"] = ([4], ["@Password"]),
    };

    /// <summary>
    /// DaffyTee's own commands that take a secret - daffytee.add_user 'bob', 'its password', ... -
    /// from the command catalog. Their connection strings are masked like any other.
    /// </summary>
    private static readonly Dictionary<string, (int[] Positions, string[] Names)> GatewayParameters =
        CommandCatalog.All
            .Where(c => c.Parameters.Any(p => p.Secret))
            .ToDictionary(
                c => c.Name,
                c => (c.Parameters.Select((p, i) => (p, i)).Where(x => x.p.Secret).Select(x => x.i + 1).ToArray(),
                      c.Parameters.Where(p => p.Secret).Select(p => "@" + p.Name).ToArray()),
                StringComparer.OrdinalIgnoreCase);

    /// <summary>A password in a connection string: Password=..., Pwd=...</summary>
    [GeneratedRegex(@"(?<!\w)(?:pwd|password)\s*=\s*", RegexOptions.IgnoreCase)]
    private static partial Regex ConnectionStringPassword();

    /// <summary>Text ending where a secret would go: "... PASSWORD = '", "ENCRYPTBYPASSPHRASE(N'".</summary>
    [GeneratedRegex(@"(?:(?:password|passwd|pwd|secret|passphrase|key_source)\w*\s*=|(?:ENCRYPTBYPASSPHRASE|DECRYPTBYPASSPHRASE|PWDCOMPARE|PWDENCRYPT)\s*\()\s*(?:N?')?\s*$",
        RegexOptions.IgnoreCase | RegexOptions.RightToLeft)]
    private static partial Regex SecretGoesHere();

    private const int MaxNesting = 3;

    /// <summary>
    /// Masks the secrets in a request's SQL and calls, in place of the originals. True when anything
    /// was masked - the event then says so, and carries no raw bytes.
    /// </summary>
    public static bool Redact(ref string? sql, ref IReadOnlyList<RpcCall>? calls, ICollection<string> maskedParameters)
    {
        bool redacted = false;

        if (sql is not null)
        {
            string masked = RedactSql(sql);
            redacted = !ReferenceEquals(masked, sql);
            sql = masked;
        }

        if (calls is not null)
        {
            List<RpcCall>? changed = null;
            for (int i = 0; i < calls.Count; i++)
            {
                var call = RedactCall(calls[i], maskedParameters);
                if (ReferenceEquals(call, calls[i])) continue;
                changed ??= [.. calls];
                changed[i] = call;
            }
            if (changed is not null)
            {
                calls = changed;
                redacted = true;
            }
        }

        return redacted;
    }

    /// <summary>Output parameters come back in the response; a secret one is masked there too.</summary>
    public static bool RedactOutputs(ResponseDetail detail, ICollection<string> maskedParameters)
    {
        bool redacted = false;
        var outputs = detail.OutputParameters;
        for (int i = 0; i < outputs.Count; i++)
        {
            var (name, value) = outputs[i];
            if (value is null || !(IsSecretName(name) || maskedParameters.Contains(name))) continue;
            outputs[i] = (name, Mask);
            redacted = true;
        }
        return redacted;
    }

    /// <summary>The SQL with its secrets masked, or the same string instance when there are none.</summary>
    public static string RedactSql(string sql) => RedactSql(sql, new HashSet<string>(StringComparer.OrdinalIgnoreCase), 0);

    private static RpcCall RedactCall(RpcCall call, ICollection<string> maskedParameters)
    {
        // Variables the statement uses in a secret's place are parameters of this call.
        var variables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? sql = call.Sql is null ? null : RedactSql(call.Sql, variables, 0);
        if (!SecretParameters.TryGetValue(LastPart(call.ProcName), out var known) && CommandParser.ProcedureName(call.ProcName) is { } command)
            GatewayParameters.TryGetValue(command, out known);

        RpcParameter[]? parameters = null;
        for (int i = 0; i < call.Parameters.Count; i++)
        {
            var p = call.Parameters[i];
            if (p.Value is null) continue;

            bool secret = p.Name.Length > 0
                ? IsSecretName(p.Name) || variables.Contains(p.Name) || known.Names?.Contains(p.Name, StringComparer.OrdinalIgnoreCase) == true
                // Unnamed values (sp_execute's, say) cannot be matched to a variable; if the statement uses one, all go.
                : variables.Count > 0 || known.Positions?.Contains(i + 1) == true;

            object? value = p.Value;
            if (secret) value = Mask;
            else if (p.Value is string text) value = RedactText(text, variables, 1);   // a job step's SQL, a connection string
            if (ReferenceEquals(value, p.Value)) continue;

            parameters ??= [.. call.Parameters];
            parameters[i] = p with { Value = value };
            if (secret && p.Name.Length > 0) maskedParameters.Add(p.Name);
        }

        if (ReferenceEquals(sql, call.Sql) && parameters is null) return call;

        // Undecoded bytes of a call that carries a secret may be the secret; they are not kept.
        return call with { Sql = sql, Parameters = parameters ?? call.Parameters, Remainder = null };
    }

    private static string RedactSql(string sql, HashSet<string> variables, int depth)
    {
        if (variables.Count == 0 && !sql.AsSpan().ContainsAny(Hints)) return sql;

        var walk = new Walk(sql, Tokenize(sql), variables, depth);

        // A variable found to carry a secret may have been given its value earlier in the text,
        // so walk again while the walk keeps finding new ones.
        for (int pass = 0; pass < 4; pass++)
        {
            int known = variables.Count;
            walk.Run();
            if (variables.Count == known) break;
        }

        return walk.Render();
    }

    /// <summary>The content of a string literal or parameter: SQL to walk, or a connection string.</summary>
    private static string RedactText(string text, HashSet<string> variables, int depth)
    {
        if (depth > MaxNesting || !text.AsSpan().ContainsAny(Hints)) return text;

        string masked = RedactSql(text, variables, depth);
        return ReferenceEquals(masked, text) ? MaskConnectionString(text) : masked;
    }

    /// <summary>
    /// Password=... in a connection string ("Server=x;User ID=u;Password=p"). Text without a ; is
    /// not taken for one, nor is a value that is a variable or an unfinished quote - that is SQL
    /// ("PASSWORD = @p", "PASSWORD = '" + ...), which the walk has already dealt with.
    /// </summary>
    internal static string MaskConnectionString(string text)
    {
        if (!text.Contains(';')) return text;
        var match = ConnectionStringPassword().Match(text);
        if (!match.Success) return text;

        var sb = new StringBuilder(text.Length);
        int at = 0;
        for (; match.Success; match = match.NextMatch())
        {
            int start = match.Index + match.Length;
            if (match.Index < at || start >= text.Length || text[start] == '@') continue;

            int end = ValueEnd(text, start);
            if (text[start] is '"' or '\'' && end == text.Length && text[^1] != text[start]) continue;

            sb.Append(text, at, start - at).Append(Mask);
            at = end;
        }
        if (at == 0) return text;
        sb.Append(text, at, text.Length - at);
        return sb.ToString();
    }

    private static int ValueEnd(string text, int start)
    {
        if (start >= text.Length) return start;
        if (text[start] is '{' or '"' or '\'')
        {
            char close = text[start] == '{' ? '}' : text[start];
            int end = text.IndexOf(close, start + 1);
            return end < 0 ? text.Length : end + 1;
        }
        int semicolon = text.IndexOf(';', start);
        return semicolon < 0 ? text.Length : semicolon;
    }

    private static bool IsSecretName(string name) => name.AsSpan().ContainsAny(SecretNames);

    /// <summary>dbo.sp_addlogin, [sys].[sp_addlogin] -> sp_addlogin.</summary>
    private static string LastPart(string name)
    {
        int dot = name.LastIndexOf('.');
        return name[(dot + 1)..].Trim('[', ']', '"');
    }

    // ---- the walk --------------------------------------------------------------------------------

    private enum Kind : byte { Word, Bracketed, DoubleQuoted, String, Binary, Number, Variable, Symbol }

    /// <summary>A token; comments and whitespace are not tokens. Closed is false for an unterminated string.</summary>
    private readonly record struct Token(Kind Kind, int Start, int Length, bool Closed = true);

    private sealed class Walk(string sql, List<Token> tokens, HashSet<string> variables, int depth)
    {
        private readonly Dictionary<int, string> _replacements = [];
        private readonly HashSet<int> _masked = [];

        public void Run()
        {
            for (int i = 0; i < tokens.Count; i++)
            {
                var t = tokens[i];
                if (t.Kind == Kind.String)
                {
                    RewriteLiteral(i);

                    // Dynamic SQL built by concatenation: N'... WITH PASSWORD = ''' + @password + ''''
                    if (Is(i + 1, '+') && EndsAtSecret(i)) Value(i + 2, mask: true);
                    continue;
                }
                if (t.Kind is not (Kind.Word or Kind.Bracketed or Kind.DoubleQuoted or Kind.Variable)) continue;

                string name = Name(t);

                // PASSWORD = '...', SECRET = N'...', @rmtpassword = '...', DECLARE @p nvarchar(50) = '...'
                if ((IsSecretName(name) || t.Kind == Kind.Variable && variables.Contains(name)) && Assignment(i) is int equals and > 0)
                {
                    Value(equals + 1, mask: true);
                    continue;
                }

                if (t.Kind == Kind.Variable) continue;

                if (Is(i + 1, '(') && SecretArguments.TryGetValue(name, out int argument))
                {
                    Argument(i + 1, argument);
                }
                else if (Is(i - 1, '.') && IsGatewaySchema(i - 2) && GatewayParameters.TryGetValue(name, out var gateway))
                {
                    ProcedureArguments(i + 1, gateway);
                }
                else if (!Is(i + 1, '.') && SecretParameters.TryGetValue(name, out var parameters))
                {
                    ProcedureArguments(i + 1, parameters);
                }
                else if (Word(i, "PRIVATE") && Word(i + 1, "KEY") && Is(i + 2, '('))
                {
                    MaskWithin(i + 2, Close(i + 2));
                }
                else if (Word(i, "OPENROWSET") && Is(i + 1, '('))
                {
                    // OPENROWSET('provider', 'server'; 'user'; 'password', 'query')
                    int end = Close(i + 1);
                    for (int k = i + 2; k < end; k++)
                        if (IsLiteral(k) && Is(k - 1, ';') && Is(k + 1, ',')) MaskToken(k);
                }
            }
        }

        public string Render()
        {
            if (_replacements.Count == 0) return sql;

            var sb = new StringBuilder(sql.Length);
            int at = 0;
            foreach (var (index, text) in _replacements.OrderBy(r => r.Key))
            {
                var t = tokens[index];
                sb.Append(sql, at, t.Start - at).Append(text);
                at = t.Start + t.Length;
            }
            sb.Append(sql, at, sql.Length - at);
            return sb.ToString();
        }

        /// <summary>
        /// Where the = after a name is: straight after it, or - for DECLARE @p nvarchar(50) = ... -
        /// after the variable's type. -1 when the name is not being given a value.
        /// </summary>
        private int Assignment(int i)
        {
            if (Is(i + 1, '=')) return i + 1;
            if (tokens[i].Kind != Kind.Variable || !(Word(i - 1, "DECLARE") || Is(i - 1, ','))) return -1;

            int parentheses = 0;
            for (int k = i + 1; k < tokens.Count && k <= i + 7; k++)
            {
                if (Is(k, '=') && parentheses == 0) return k;
                if (Is(k, '(')) parentheses++;
                else if (Is(k, ')')) parentheses--;
                else if (tokens[k].Kind is Kind.Word or Kind.Number || Is(k, ',') && parentheses > 0) continue;
                else return -1;
            }
            return -1;
        }

        /// <summary>
        /// One value starting at k - a literal, a variable, a parenthesised expression, a function
        /// call, joined by + - masked (and its variables remembered) or just stepped over. Returns
        /// the index after it.
        /// </summary>
        private int Value(int k, bool mask)
        {
            while (k < tokens.Count)
            {
                if (Is(k, '-')) k++;
                if (k >= tokens.Count) return k;

                var t = tokens[k];
                if (IsLiteral(k))
                {
                    if (mask) MaskToken(k);
                    k++;
                }
                else if (t.Kind == Kind.Variable)
                {
                    if (mask) variables.Add(Name(t));
                    k++;
                }
                else if (Is(k, '(') || Is(k, '{'))
                {
                    int end = Close(k);
                    if (mask) MaskWithin(k, end);
                    k = end + 1;
                }
                else if (t.Kind is Kind.Word or Kind.Bracketed && Is(k + 1, '('))
                {
                    int end = Close(k + 1);
                    if (mask) MaskWithin(k + 1, end);
                    k = end + 1;
                }
                else if (t.Kind is Kind.Word or Kind.Bracketed or Kind.Number)
                {
                    k++;   // a column, NULL, DEFAULT, a number: nothing to hide
                }
                else
                {
                    return k;
                }

                if (!Is(k, '+')) return k;
                k++;
            }
            return k;
        }

        /// <summary>The given argument (from 1) of the call whose ( is at open.</summary>
        private void Argument(int open, int argument)
        {
            int nesting = 1, current = 1;
            for (int k = open + 1; k < tokens.Count; k++)
            {
                if (nesting == 1 && current == argument && (k == open + 1 || Is(k - 1, ',')))
                {
                    if (!Is(k, ')')) Value(k, mask: true);
                    return;
                }
                if (Is(k, '(')) nesting++;
                else if (Is(k, ')') && --nesting == 0) return;
                else if (nesting == 1 && Is(k, ',')) current++;
            }
        }

        /// <summary>EXEC sp_addlogin 'bob', 'secret' / EXEC sp_addlogin @loginame = 'bob', @passwd = 'secret'</summary>
        private void ProcedureArguments(int k, (int[] Positions, string[] Names) secret)
        {
            for (int position = 1; k < tokens.Count; position++)
            {
                string? named = null;
                if (tokens[k].Kind == Kind.Variable && Is(k + 1, '='))
                {
                    named = Name(tokens[k]);
                    k += 2;
                }

                bool mask = named is null
                    ? secret.Positions.Contains(position)
                    : secret.Names.Contains(named, StringComparer.OrdinalIgnoreCase) || IsSecretName(named);
                int next = Value(k, mask);
                if (next == k) return;
                k = next;

                if (Word(k, "OUTPUT") || Word(k, "OUT")) k++;
                if (!Is(k, ',')) return;
                k++;
            }
        }

        private void MaskWithin(int open, int close)
        {
            for (int k = open + 1; k < close && k < tokens.Count; k++)
            {
                if (IsLiteral(k)) MaskToken(k);
                else if (tokens[k].Kind == Kind.Variable) variables.Add(Name(tokens[k]));
            }
        }

        private void MaskToken(int k)
        {
            var t = tokens[k];

            // The opening quote of a string another string closes: 'PASSWORD = ''' + @p + ''''
            if (!t.Closed && t.Start + t.Length - sql.IndexOf('\'', t.Start) <= 1) return;
            if (!_masked.Add(k)) return;

            _replacements[k] = t.Kind switch
            {
                Kind.DoubleQuoted => $"\"{Mask}\"",
                Kind.String => (sql[t.Start] is 'N' or 'n' ? "N'" : "'") + Mask + (t.Closed ? "'" : ""),
                _ => $"'{Mask}'",
            };
        }

        /// <summary>A string that is itself SQL (EXEC('...'), a job step) or a connection string.</summary>
        private void RewriteLiteral(int k)
        {
            if (_masked.Contains(k)) return;

            string content = Content(k);
            string rewritten = RedactText(content, variables, depth + 1);
            if (ReferenceEquals(rewritten, content)) return;

            var t = tokens[k];
            int quote = sql.IndexOf('\'', t.Start);
            _replacements[k] = sql[t.Start..(quote + 1)] + rewritten.Replace("'", "''") + (t.Closed ? "'" : "");
        }

        /// <summary>The string's SQL stops just where a secret goes, and the secret is concatenated on.</summary>
        private bool EndsAtSecret(int k)
        {
            string content = Content(k);
            return content.AsSpan().ContainsAny(Hints) && SecretGoesHere().IsMatch(content);
        }

        /// <summary>What a string literal says: without its quotes, and with doubled quotes single.</summary>
        private string Content(int k)
        {
            var t = tokens[k];
            int quote = sql.IndexOf('\'', t.Start);
            int end = t.Start + t.Length - (t.Closed ? 1 : 0);
            return sql[(quote + 1)..end].Replace("''", "'");
        }

        private int Close(int open)
        {
            char opening = sql[tokens[open].Start];
            char closing = opening == '{' ? '}' : ')';
            int nesting = 0;
            for (int k = open; k < tokens.Count; k++)
            {
                if (Is(k, opening)) nesting++;
                else if (Is(k, closing) && --nesting == 0) return k;
            }
            return tokens.Count;
        }

        private bool Is(int k, char symbol) =>
            k >= 0 && k < tokens.Count && tokens[k].Kind == Kind.Symbol && sql[tokens[k].Start] == symbol;

        private bool Word(int k, string word) =>
            k >= 0 && k < tokens.Count && tokens[k].Kind == Kind.Word
            && sql.AsSpan(tokens[k].Start, tokens[k].Length).Equals(word, StringComparison.OrdinalIgnoreCase);

        private bool IsLiteral(int k) => tokens[k].Kind is Kind.String or Kind.DoubleQuoted or Kind.Binary;

        private bool IsGatewaySchema(int k) =>
            k >= 0 && tokens[k].Kind is Kind.Word or Kind.Bracketed or Kind.DoubleQuoted
            && Name(tokens[k]).Equals(CommandParser.Schema, StringComparison.OrdinalIgnoreCase);

        private string Name(Token t) => t.Kind switch
        {
            Kind.Bracketed => sql.Substring(t.Start + 1, Math.Max(0, t.Length - 2)).Replace("]]", "]"),
            Kind.DoubleQuoted => sql.Substring(t.Start + 1, Math.Max(0, t.Length - 2)).Replace("\"\"", "\""),
            _ => sql.Substring(t.Start, t.Length),
        };
    }

    // ---- tokens ----------------------------------------------------------------------------------

    private static List<Token> Tokenize(string sql)
    {
        var tokens = new List<Token>();
        int i = 0, n = sql.Length;
        while (i < n)
        {
            char c = sql[i];
            int start = i;

            if (char.IsWhiteSpace(c))
            {
                i++;
            }
            else if (c == '-' && i + 1 < n && sql[i + 1] == '-')
            {
                int newline = sql.IndexOf('\n', i);
                i = newline < 0 ? n : newline + 1;
            }
            else if (c == '/' && i + 1 < n && sql[i + 1] == '*')
            {
                i = BlockCommentEnd(sql, i);
            }
            else if (c == '\'' || c is 'N' or 'n' && i + 1 < n && sql[i + 1] == '\'')
            {
                (i, bool closed) = QuotedEnd(sql, c == '\'' ? i : i + 1, '\'');
                tokens.Add(new Token(Kind.String, start, i - start, closed));
            }
            else if (c is '"' or '[')
            {
                (i, _) = QuotedEnd(sql, i, c == '[' ? ']' : '"');
                tokens.Add(new Token(c == '[' ? Kind.Bracketed : Kind.DoubleQuoted, start, i - start));
            }
            else if (c == '0' && i + 1 < n && sql[i + 1] is 'x' or 'X')
            {
                i += 2;
                while (i < n && char.IsAsciiHexDigit(sql[i])) i++;
                tokens.Add(new Token(Kind.Binary, start, i - start));
            }
            else if (char.IsAsciiDigit(c))
            {
                while (i < n && (char.IsAsciiLetterOrDigit(sql[i]) || sql[i] == '.')) i++;
                tokens.Add(new Token(Kind.Number, start, i - start));
            }
            else if (c == '@' || char.IsLetter(c) || c is '_' or '#')
            {
                i++;
                while (i < n && (char.IsLetterOrDigit(sql[i]) || sql[i] is '_' or '#' or '$' or '@')) i++;
                tokens.Add(new Token(c == '@' ? Kind.Variable : Kind.Word, start, i - start));
            }
            else
            {
                i++;
                tokens.Add(new Token(Kind.Symbol, start, 1));
            }
        }
        return tokens;
    }

    /// <summary>After the closing quote; a doubled quote is an escaped one. Unterminated runs to the end.</summary>
    private static (int End, bool Closed) QuotedEnd(string sql, int open, char close)
    {
        int i = open + 1;
        while (i < sql.Length)
        {
            int found = sql.IndexOf(close, i);
            if (found < 0) break;
            if (found + 1 < sql.Length && sql[found + 1] == close)
            {
                i = found + 2;
                continue;
            }
            return (found + 1, true);
        }
        return (sql.Length, false);
    }

    /// <summary>T-SQL block comments nest.</summary>
    private static int BlockCommentEnd(string sql, int i)
    {
        int nesting = 0;
        while (i < sql.Length - 1)
        {
            if (sql[i] == '/' && sql[i + 1] == '*') { nesting++; i += 2; }
            else if (sql[i] == '*' && sql[i + 1] == '/') { i += 2; if (--nesting == 0) return i; }
            else i++;
        }
        return sql.Length;
    }
}
