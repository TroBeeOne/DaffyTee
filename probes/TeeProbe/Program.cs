using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;
using Microsoft.Data.SqlClient.Server;

namespace TeeProbe;

/// <summary>
/// Points a real Microsoft.Data.SqlClient at DaffyTee and proves the relay is transparent: every
/// check asserts a value that came back, not merely that nothing threw. Where it matters, the same
/// thing is asked of the target directly and the answers compared.
///
///   TeeProbe [--port 14330] [--user bob] [--password bobpass123] [--target-login svc_app]
///            [--direct "connection string to the target"] [--cert FILE.cer] [--only NAME] [--bench]
///            [--expect-trust yes|no] [--gateway admin|user] [--non-admin USER:PASSWORD]
///
/// --expect-trust adds a check that connects without TrustServerCertificate, as Power BI's connector
/// does: 'no' expects the refusal a certificate Windows does not trust earns, 'yes' expects a login.
///
/// --gateway adds the daffytee.* command checks, for a --user that is a DaffyTee admin or not;
/// --non-admin names a second login, which must not be one, to check the refusal from.
/// </summary>
internal static class Program
{
    private static int _passed, _failed;
    private static string _proxy = "", _direct = "", _user = "", _password = "", _targetLogin = "", _certificate = "";
    private static string? _expectTrust, _gateway, _nonAdmin;

    private static async Task<int> Main(string[] args)
    {
        int port = 14330;
        string? only = null, consume = null, queue = "daffytee.all";
        bool bench = false, purge = false, churn = false;
        int show = 0, load = 0, sessions = 1;
        _user = "bob";
        _password = "bobpass123";
        _targetLogin = "svc_app";
        _direct = "Server=tcp:127.0.0.1,14333;User Id=svc_app;Password=Svc!App2026;Initial Catalog=DaffyTeeTest;" +
                  "Encrypt=Mandatory;TrustServerCertificate=True;Pooling=False";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--port": port = int.Parse(args[++i]); break;
                case "--user": _user = args[++i]; break;
                case "--password": _password = args[++i]; break;
                case "--target-login": _targetLogin = args[++i]; break;
                case "--direct": _direct = args[++i]; break;
                case "--cert": _certificate = args[++i]; break;
                case "--expect-trust": _expectTrust = args[++i]; break;
                case "--gateway": _gateway = args[++i]; break;
                case "--non-admin": _nonAdmin = args[++i]; break;
                case "--only": only = args[++i]; break;
                case "--bench": bench = true; break;
                case "--consume": consume = args[++i]; break;
                case "--queue": queue = args[++i]; break;
                case "--purge": purge = true; break;
                case "--show": show = int.Parse(args[++i]); break;
                case "--load": load = int.Parse(args[++i]); break;
                case "--sessions": sessions = int.Parse(args[++i]); break;
                case "--churn": churn = true; break;
                default: Console.Error.WriteLine($"unknown argument {args[i]}"); return 1;
            }
        }

        if (consume is not null) return await Consumer.RunAsync(consume, queue, purge, show);

        _proxy = $"Server=tcp:127.0.0.1,{port};User Id={_user};Password={_password};Encrypt=Mandatory;" +
                 "TrustServerCertificate=True;Pooling=False;Application Name=TeeProbe;Connect Timeout=15";

        var checks = new (string Name, Func<Task> Run)[]
        {
            ("identity", Identity),
            ("encrypt-optional-upgraded", EncryptOptionalUpgraded),
            ("encrypt-strict", EncryptStrict),
            ("bad-password", BadPassword),
            ("unknown-user", UnknownUser),
            ("types", Types),
            ("parameters", Parameters),
            ("max-parameters", MaxParameters),
            ("proc-output-return", ProcOutputAndReturn),
            ("tvp", TableValuedParameter),
            ("errors", Errors),
            ("multiple-results", MultipleResults),
            ("transactions", Transactions),
            ("pooling-reset", PoolingReset),
            ("cancel", Cancel),
            ("timeout", CommandTimeout),
            ("bulk-copy", BulkCopy),
            ("mars", Mars),
            ("use-database", UseDatabase),
            ("large-result", LargeResult),
            ("prepared", Prepared),
        };
        if (_expectTrust is not null) checks = [.. checks, ("validated-certificate", ValidatedCertificate)];
        if (_gateway is not null)
        {
            checks =
            [
                .. checks,
                ("gateway-status", GatewayStatus),
                ("gateway-procedure-call", GatewayProcedureCall),
                ("gateway-refusals", GatewayRefusals),
                ("gateway-pooled-reset", GatewayPooledReset),
                ("gateway-in-transaction", GatewayInTransaction),
                ("gateway-not-a-command", GatewayNotACommand),
                ("gateway-mars", GatewayMars),
                ("gateway-column-encryption", GatewayColumnEncryption),
                ("gateway-admin", GatewayAdmin),
            ];
            if (_nonAdmin is not null) checks = [.. checks, ("gateway-non-admin", GatewayNonAdmin)];
        }

        foreach (var (name, run) in checks)
        {
            if (only is not null && !name.Contains(only, StringComparison.OrdinalIgnoreCase)) continue;
            await Check(name, run);
        }

        if (bench) await Benchmark();
        if (load > 0 && sessions == 1) await Load(load);
        if (load > 0 && sessions > 1 && !churn) await ParallelLoad(load, sessions);
        if (load > 0 && churn) await Churn(load, sessions);

        Console.WriteLine();
        Console.WriteLine($"{_passed} passed, {_failed} failed");
        return _failed == 0 ? 0 : 1;
    }

    private static async Task Check(string name, Func<Task> run)
    {
        var stopwatch = Stopwatch.StartNew();
        try
        {
            await run();
            _passed++;
            Console.WriteLine($"ok    {name,-28} {stopwatch.ElapsedMilliseconds,6} ms");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"FAIL  {name,-28} {stopwatch.ElapsedMilliseconds,6} ms  {ex.GetType().Name}: {ex.Message.ReplaceLineEndings(" ")}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private static void Equal<T>(T expected, T actual, string what)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new Exception($"{what}: expected <{expected}>, got <{actual}>");
    }

    private static async Task<SqlConnection> OpenAsync(string? connectionString = null)
    {
        var connection = new SqlConnection(connectionString ?? _proxy);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<object?> ScalarAsync(SqlConnection connection, string sql)
    {
        await using var command = new SqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is DBNull ? null : value;
    }

    // ---- checks ----------------------------------------------------------------------------------

    private static async Task Identity()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            "SELECT SUSER_SNAME(), HOST_NAME(), APP_NAME(), DB_NAME(), @@SPID, SERVERPROPERTY('ProductVersion')", connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();

        Equal(_targetLogin, reader.GetString(0), "SUSER_SNAME() is the mapped target login");
        Equal(Environment.MachineName, reader.GetString(1), "HOST_NAME() is the client's host, passed through");
        Equal("TeeProbe", reader.GetString(2), "APP_NAME() is the client's application name, passed through");
        Equal("DaffyTeeTest", reader.GetString(3), "DB_NAME() is the mapping's default database");
        Equal(reader.GetInt16(4), (short)connection.ServerProcessId, "@@SPID matches what the client was told");
        Assert(connection.ServerVersion.StartsWith("16."), $"client sees the real server version, got {connection.ServerVersion}");
    }

    private static async Task EncryptOptionalUpgraded()
    {
        var builder = new SqlConnectionStringBuilder(_proxy) { Encrypt = SqlConnectionEncryptOption.Optional };
        await using var connection = await OpenAsync(builder.ConnectionString);
        Equal(_targetLogin, (string?)await ScalarAsync(connection, "SELECT SUSER_SNAME()"), "login through an upgraded Optional client");
    }

    private static async Task EncryptStrict()
    {
        if (_certificate.Length == 0) throw new Exception("pass --cert FILE.cer to check Encrypt=Strict");

        var builder = new SqlConnectionStringBuilder(_proxy)
        {
            Encrypt = SqlConnectionEncryptOption.Strict,
            TrustServerCertificate = false,
            ServerCertificate = _certificate,
        };
        await using var connection = await OpenAsync(builder.ConnectionString);
        Equal(_targetLogin, (string?)await ScalarAsync(connection, "SELECT SUSER_SNAME()"), "login over TDS 8.0");
    }

    /// <summary>
    /// Encrypt=Mandatory and Encrypt=Strict with the certificate checked by Windows alone - no
    /// TrustServerCertificate, no ServerCertificate file - which is how a client that cannot be told
    /// otherwise connects.
    /// </summary>
    private static async Task ValidatedCertificate()
    {
        bool trusted = _expectTrust == "yes";
        foreach (var (label, encrypt) in new[] { ("Mandatory", SqlConnectionEncryptOption.Mandatory), ("Strict", SqlConnectionEncryptOption.Strict) })
        {
            var builder = new SqlConnectionStringBuilder(_proxy) { Encrypt = encrypt, TrustServerCertificate = false };
            try
            {
                await using var connection = await OpenAsync(builder.ConnectionString);
                Assert(trusted, $"Encrypt={label} connected without TrustServerCertificate, to a certificate that should not be trusted");
                Equal(_targetLogin, (string?)await ScalarAsync(connection, "SELECT SUSER_SNAME()"), $"login with the certificate validated (Encrypt={label})");
            }
            catch (SqlException ex) when (!trusted)
            {
                Assert(ex.Message.Contains("not trusted", StringComparison.OrdinalIgnoreCase),
                       $"Encrypt={label} should be refused for the certificate, got: {ex.Message}");
            }
        }
    }

    // ---- daffytee.* commands, answered by DaffyTee itself ------------------------------------------

    /// <summary>daffytee.status is DaffyTee's answer, and agrees with what the target says about the same session.</summary>
    private static async Task GatewayStatus()
    {
        await using var connection = await OpenAsync();
        var messages = new List<string>();
        connection.InfoMessage += (_, e) => messages.Add(e.Message);

        await using (var command = new SqlCommand("EXEC daffytee.status", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert(await reader.ReadAsync(), "daffytee.status returns a row");
            Equal(_user, reader.GetString(reader.GetOrdinal("login")), "login");
            Equal(_targetLogin, reader.GetString(reader.GetOrdinal("target_login")), "target_login");
            Equal(connection.ServerProcessId, reader.GetInt32(reader.GetOrdinal("spid")), "spid is this session's SPID on the target");
            Equal(_gateway == "admin" ? "yes" : "no", reader.GetString(reader.GetOrdinal("admin")), "admin");
            Assert(!await reader.ReadAsync(), "daffytee.status returns one row");
        }

        Assert(messages.Any(m => m.StartsWith("You are session", StringComparison.Ordinal)), $"the message says who you are, got: {string.Join(" | ", messages)}");
        Equal(connection.ServerProcessId, (int)(short)(await ScalarAsync(connection, "SELECT @@SPID"))!, "the session carries on with the target afterwards");
    }

    /// <summary>From code a command is a stored procedure call: parameters by name, and a return value of 0.</summary>
    private static async Task GatewayProcedureCall()
    {
        await using var connection = await OpenAsync();
        await using (var command = new SqlCommand("daffytee.help", connection) { CommandType = CommandType.StoredProcedure })
        {
            command.Parameters.AddWithValue("@command", "status");
            var returned = command.Parameters.Add("@return", SqlDbType.Int);
            returned.Direction = ParameterDirection.ReturnValue;

            var rows = new List<string>();
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) rows.Add(reader.GetString(0));

            Equal("daffytee.status", string.Join(",", rows), "daffytee.help @command = 'status'");
            Equal(0, (int)returned.Value, "return value");
        }

        await using var bad = new SqlCommand("daffytee.kill_session", connection) { CommandType = CommandType.StoredProcedure };
        bad.Parameters.AddWithValue("@session", 999_999);
        Equal(_gateway == "admin" ? 50204 : 50203, (await SqlErrorAsync(() => bad.ExecuteNonQueryAsync())).Number,
              "a refused procedure call is an ordinary SqlException");
    }

    private static async Task GatewayRefusals()
    {
        await using var connection = await OpenAsync();
        Equal(50201, (await SqlErrorAsync(() => ScalarAsync(connection, "SELECT 1; EXEC daffytee.status"))).Number, "a command sharing its batch");
        Equal(50202, (await SqlErrorAsync(() => ScalarAsync(connection, "EXEC daffytee.no_such_command"))).Number, "an unknown command");
        Equal(50202, (await SqlErrorAsync(() => ScalarAsync(connection, "EXEC daffytee.help 'a', 'b'"))).Number, "too many arguments");
        if (_gateway == "admin")
            Equal(50205, (await SqlErrorAsync(() => ScalarAsync(connection, "EXEC daffytee.set_sample 0"))).Number, "capture is not switched off from T-SQL");
        Equal(1, (int)(await ScalarAsync(connection, "SELECT 1"))!, "the session is fine after refusals");
    }

    /// <summary>
    /// The first request on a reused pooled connection carries the reset. When that request is a
    /// command, which the target never sees, the reset must still happen: DaffyTee sends it on a
    /// request of its own.
    /// </summary>
    private static async Task GatewayPooledReset()
    {
        var builder = new SqlConnectionStringBuilder(_proxy) { Pooling = true, MaxPoolSize = 1, ApplicationName = "TeeProbe-gw-" + Guid.NewGuid().ToString("N")[..6] };
        short firstSpid;

        await using (var connection = await OpenAsync(builder.ConnectionString))
        {
            firstSpid = (short)(await ScalarAsync(connection, "CREATE TABLE #leftover (x int); SET LANGUAGE Deutsch; SELECT @@SPID"))!;
        }

        await using (var connection = await OpenAsync(builder.ConnectionString))
        {
            await using (var command = new SqlCommand("EXEC daffytee.status", connection))
            await using (var reader = await command.ExecuteReaderAsync())
            {
                Assert(await reader.ReadAsync(), "daffytee.status answered on the reused connection");
                Equal((int)firstSpid, reader.GetInt32(reader.GetOrdinal("spid")), "the same upstream session");
            }

            Equal(null, await ScalarAsync(connection, "SELECT OBJECT_ID('tempdb..#leftover')"), "temp table dropped by the reset");
            Equal("us_english", (string?)await ScalarAsync(connection, "SELECT @@LANGUAGE"), "language restored by the reset");
        }

        SqlConnection.ClearPool(new SqlConnection(builder.ConnectionString));
    }

    private static async Task GatewayInTransaction()
    {
        await using var connection = await OpenAsync();
        await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();

        await using (var command = new SqlCommand("EXEC daffytee.status", connection, transaction))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert(await reader.ReadAsync(), "daffytee.status answered inside a transaction");
            Equal(_user, reader.GetString(reader.GetOrdinal("login")), "login");
        }

        await using (var count = new SqlCommand("SELECT @@TRANCOUNT", connection, transaction))
            Equal(1, (int)(await count.ExecuteScalarAsync())!, "the transaction is still open on the target");
        await transaction.CommitAsync();
    }

    /// <summary>
    /// Requests that mention daffytee without being a command reach the target unchanged - including
    /// one longer than DaffyTee holds back to look at, whose first part waits and whose rest follows.
    /// </summary>
    private static async Task GatewayNotACommand()
    {
        await using var connection = await OpenAsync();
        Equal("EXEC daffytee.status", (string?)await ScalarAsync(connection, "SELECT 'EXEC daffytee.status'"), "a command inside a string");
        Equal(1, (int)(await ScalarAsync(connection, "-- EXEC daffytee.status\nSELECT 1"))!, "a command inside a comment");

        string several = new('x', 20_000);
        Equal(20_008L, Convert.ToInt64(await ScalarAsync(connection, $"SELECT LEN('daffytee' + '{several}')")), "a batch of several packets");

        string beyond = new('y', 50_000);
        Equal(50_008L, Convert.ToInt64(await ScalarAsync(connection, $"SELECT LEN(N'daffytee' + N'{beyond}')")), "a batch beyond what is held back");

        await using var procedure = new SqlCommand("dbo.daffytee_nothing", connection) { CommandType = CommandType.StoredProcedure };
        Equal(2812, (await SqlErrorAsync(() => procedure.ExecuteNonQueryAsync())).Number, "a procedure merely named like one is the target's to refuse");
    }

    /// <summary>Under MARS commands are not DaffyTee's to answer: they reach the target, which does not know them.</summary>
    private static async Task GatewayMars()
    {
        var builder = new SqlConnectionStringBuilder(_proxy) { MultipleActiveResultSets = true };
        await using var connection = await OpenAsync(builder.ConnectionString);
        Equal(2812, (await SqlErrorAsync(() => ScalarAsync(connection, "EXEC daffytee.status"))).Number, "Could not find stored procedure, from the target");
        Equal(1, (int)(await ScalarAsync(connection, "SELECT 1"))!, "and the session carries on");
    }

    /// <summary>With Always Encrypted on, every COLMETADATA carries a key table - DaffyTee's own included.</summary>
    private static async Task GatewayColumnEncryption()
    {
        var builder = new SqlConnectionStringBuilder(_proxy) { ColumnEncryptionSetting = SqlConnectionColumnEncryptionSetting.Enabled };
        await using var connection = await OpenAsync(builder.ConnectionString);
        await using (var command = new SqlCommand("EXEC daffytee.status", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert(await reader.ReadAsync(), "daffytee.status answered with column encryption on");
            Equal(_user, reader.GetString(reader.GetOrdinal("login")), "login");
        }
        Equal(1, (int)(await ScalarAsync(connection, "SELECT 1"))!, "and the session carries on");
    }

    private static async Task GatewayAdmin()
    {
        await using var connection = await OpenAsync();
        if (_gateway != "admin")
        {
            Equal(50203, (await SqlErrorAsync(() => ScalarAsync(connection, "EXEC daffytee.sessions"))).Number, "admin commands need an admin");
            return;
        }

        int me = (int)(await ScalarAsync(connection, "EXEC daffytee.status"))!;
        Assert((await ColumnAsync(connection, "EXEC daffytee.sessions", "session")).Contains(me), "daffytee.sessions lists this session");
        Equal(1, (await ColumnAsync(connection, "EXEC daffytee.health", "version")).Count, "daffytee.health is one row");
        Assert((await ColumnAsync(connection, "EXEC daffytee.users", "login")).Contains(_user), "daffytee.users lists this login");
        Assert((await ColumnAsync(connection, "EXEC daffytee.settings", "setting")).Contains("tee"), "daffytee.settings lists the tee");
        Assert((await ColumnAsync(connection, "EXEC daffytee.log 5", "event")).Count > 0, "daffytee.log has lines");
        Assert((await ColumnAsync(connection, "EXEC daffytee.recent 5", "type")).Count > 0, "daffytee.recent has events");

        // The password here must not reach any event (verify-relay looks).
        Equal(50204, (await SqlErrorAsync(() => ScalarAsync(connection, "EXEC daffytee.set_user_password 'no_such_login', 'Secret#Probe1'"))).Number,
              "no such login");
        Equal(50205, (await SqlErrorAsync(() => ScalarAsync(connection, $"EXEC daffytee.kill_session {me}"))).Number, "not your own session");

        await using var victim = await OpenAsync();
        int victimId = (int)(await ScalarAsync(victim, "EXEC daffytee.status"))!;
        await ScalarAsync(connection, $"EXEC daffytee.kill_session {victimId}");
        try
        {
            await ScalarAsync(victim, "SELECT 1");
            throw new Exception("a killed session still answered");
        }
        catch (SqlException) { }
        Assert(!(await ColumnAsync(connection, "EXEC daffytee.sessions", "session")).Contains(victimId), "the killed session is gone");
    }

    private static async Task GatewayNonAdmin()
    {
        string[] parts = _nonAdmin!.Split(':', 2);
        var builder = new SqlConnectionStringBuilder(_proxy) { UserID = parts[0], Password = parts[1] };
        await using var connection = await OpenAsync(builder.ConnectionString);

        await using (var command = new SqlCommand("EXEC daffytee.status", connection))
        await using (var reader = await command.ExecuteReaderAsync())
        {
            Assert(await reader.ReadAsync(), "daffytee.status is for everyone");
            Equal("no", reader.GetString(reader.GetOrdinal("admin")), "admin");
        }

        var listed = await ColumnAsync(connection, "EXEC daffytee.help", "command");
        Equal("daffytee.help,daffytee.status", string.Join(",", listed), "help lists only what this login may run");
        Equal(50203, (await SqlErrorAsync(() => ScalarAsync(connection, "EXEC daffytee.sessions"))).Number, "admin commands need an admin");
    }

    private static async Task<SqlException> SqlErrorAsync(Func<Task> run)
    {
        try
        {
            await run();
        }
        catch (SqlException ex)
        {
            return ex;
        }
        throw new Exception("expected a SqlException, but it succeeded");
    }

    private static async Task<List<object>> ColumnAsync(SqlConnection connection, string sql, string column)
    {
        var values = new List<object>();
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        int ordinal = reader.GetOrdinal(column);
        while (await reader.ReadAsync()) values.Add(reader.GetValue(ordinal));
        return values;
    }

    private static async Task BadPassword()
    {
        var builder = new SqlConnectionStringBuilder(_proxy) { Password = "definitely-wrong" };
        try
        {
            await using var connection = await OpenAsync(builder.ConnectionString);
            throw new Exception("a wrong password was accepted");
        }
        catch (SqlException ex)
        {
            Equal(18456, ex.Number, "error number");
            Assert(ex.Message.Contains($"'{_user}'"), $"message names the DaffyTee login: {ex.Message}");
            Assert(!ex.Message.Contains(_targetLogin, StringComparison.OrdinalIgnoreCase), "message must not leak the target login");
        }
    }

    private static async Task UnknownUser()
    {
        var builder = new SqlConnectionStringBuilder(_proxy) { UserID = "no_such_user_" + Guid.NewGuid().ToString("N")[..6] };
        try
        {
            await using var connection = await OpenAsync(builder.ConnectionString);
            throw new Exception("an unknown user was accepted");
        }
        catch (SqlException ex)
        {
            Equal(18456, ex.Number, "error number");
        }
    }

    private const string TypesQuery = """
        SELECT CAST(42 AS int) AS i, CAST(9223372036854775807 AS bigint) AS bi, CAST(7 AS tinyint) AS ti,
               CAST(-12 AS smallint) AS si, CAST(12345.6789 AS decimal(18,4)) AS dec,
               CAST(99.99 AS money) AS m, CAST(1.5e10 AS float) AS f, CAST(2.5 AS real) AS r,
               CAST(1 AS bit) AS b, CAST('2026-09-23' AS date) AS d, CAST('13:14:15.1234567' AS time(7)) AS t,
               CAST('2026-09-23T13:14:15.123' AS datetime) AS dt, CAST('2026-09-23T13:14:15.1234567' AS datetime2(7)) AS dt2,
               CAST('2026-09-23T13:14:15.1234567+02:00' AS datetimeoffset(7)) AS dto,
               CAST('6F9619FF-8B86-D011-B42D-00C04FC964FF' AS uniqueidentifier) AS g,
               N'Zoë Ünïcode ✓' AS nv, 'plain ascii' AS v, CAST(0xDEADBEEF AS varbinary(8)) AS vb,
               CAST('<a b="1">x</a>' AS xml) AS x, CAST(123 AS sql_variant) AS sv,
               REPLICATE(CAST(N'ab' AS nvarchar(max)), 100000) AS big, CAST(NULL AS int) AS nothing
        """;

    private static async Task Types()
    {
        var viaProxy = await ReadRowAsync(_proxy, TypesQuery);
        var direct = await ReadRowAsync(_direct, TypesQuery);

        Equal(direct.Length, viaProxy.Length, "column count");
        for (int i = 0; i < direct.Length; i++)
            Equal(Render(direct[i]), Render(viaProxy[i]), $"column {i}");

        Equal(200_000, ((string)viaProxy[20]!).Length, "nvarchar(max) length");
    }

    private static async Task<object?[]> ReadRowAsync(string connectionString, string sql)
    {
        await using var connection = await OpenAsync(connectionString);
        await using var command = new SqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        var values = new object?[reader.FieldCount];
        for (int i = 0; i < values.Length; i++) values[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
        return values;
    }

    private static string Render(object? value) => value switch
    {
        null => "NULL",
        byte[] bytes => Convert.ToHexString(bytes),
        string s when s.Length > 64 => $"{s.Length}:{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s)))}",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    private static async Task Parameters()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand(
            "SELECT @i + 1, @s + N'!', @d * 2, @dt, @g, @b, @bin, @n, @dtm", connection);
        command.Parameters.Add("@i", SqlDbType.Int).Value = 41;
        command.Parameters.Add("@s", SqlDbType.NVarChar, 50).Value = "héllo";
        command.Parameters.Add("@d", SqlDbType.Decimal).Value = 10.25m;
        command.Parameters["@d"].Precision = 18;
        command.Parameters["@d"].Scale = 2;
        command.Parameters.Add("@dt", SqlDbType.DateTime2).Value = new DateTime(2026, 9, 23, 1, 2, 3);
        command.Parameters.Add("@g", SqlDbType.UniqueIdentifier).Value = Guid.Parse("11111111-2222-3333-4444-555555555555");
        command.Parameters.Add("@b", SqlDbType.Bit).Value = true;
        command.Parameters.Add("@bin", SqlDbType.VarBinary, 16).Value = new byte[] { 1, 2, 3 };
        command.Parameters.Add("@n", SqlDbType.Int).Value = DBNull.Value;
        // datetime (not datetime2) in the afternoon: DaffyTee 0.3.0 decoded any time after 00:02:23 wrong.
        command.Parameters.Add("@dtm", SqlDbType.DateTime).Value = new DateTime(2026, 9, 23, 13, 14, 15, 123);

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        Equal(42, reader.GetInt32(0), "int");
        Equal("héllo!", reader.GetString(1), "nvarchar");
        Equal(20.50m, reader.GetDecimal(2), "decimal");
        Equal(new DateTime(2026, 9, 23, 1, 2, 3), reader.GetDateTime(3), "datetime2");
        Equal(Guid.Parse("11111111-2222-3333-4444-555555555555"), reader.GetGuid(4), "uniqueidentifier");
        Equal(true, reader.GetBoolean(5), "bit");
        Equal("010203", Convert.ToHexString((byte[])reader.GetValue(6)), "varbinary");
        Assert(reader.IsDBNull(7), "NULL parameter");
        Equal(new DateTime(2026, 9, 23, 13, 14, 15, 123), reader.GetDateTime(8), "datetime");
    }

    private static async Task MaxParameters()
    {
        string text = new('x', 5_000_000);
        byte[] blob = RandomNumberGenerator.GetBytes(10_000_000);

        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("SELECT LEN(@t), DATALENGTH(@b), HASHBYTES('SHA2_256', @b)", connection);
        command.Parameters.Add("@t", SqlDbType.NVarChar, -1).Value = text;
        command.Parameters.Add("@b", SqlDbType.VarBinary, -1).Value = blob;

        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        Equal((long)text.Length, Convert.ToInt64(reader.GetValue(0)), "nvarchar(max) parameter length");
        Equal((long)blob.Length, Convert.ToInt64(reader.GetValue(1)), "varbinary(max) parameter length");
        Equal(Convert.ToHexString(SHA256.HashData(blob)), Convert.ToHexString((byte[])reader.GetValue(2)), "varbinary(max) hash");
    }

    private static async Task ProcOutputAndReturn()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("dbo.GetCustomer", connection) { CommandType = CommandType.StoredProcedure };
        command.Parameters.Add("@Id", SqlDbType.Int).Value = 2;
        var name = command.Parameters.Add("@Name", SqlDbType.NVarChar, 100);
        name.Direction = ParameterDirection.Output;
        var result = command.Parameters.Add("@RETURN_VALUE", SqlDbType.Int);
        result.Direction = ParameterDirection.ReturnValue;

        int rows = 0;
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync()) rows++;

        Equal(1, rows, "result set rows");
        Equal("Grace Hopper", name.Value as string, "output parameter");
        Equal(0, (int)result.Value, "return value");
    }

    private static async Task TableValuedParameter()
    {
        var ids = new DataTable();
        ids.Columns.Add("Id", typeof(int));
        foreach (int id in new[] { 1, 3, 4 }) ids.Rows.Add(id);

        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("dbo.GetCustomersByIds", connection) { CommandType = CommandType.StoredProcedure };
        var parameter = command.Parameters.AddWithValue("@Ids", ids);
        parameter.SqlDbType = SqlDbType.Structured;
        parameter.TypeName = "dbo.IdList";

        var seen = new List<int>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync()) seen.Add(reader.GetInt32(0));
        Equal("1,3,4", string.Join(",", seen), "rows selected by the TVP");
    }

    private static async Task Errors()
    {
        await using var connection = await OpenAsync();
        try
        {
            await ScalarAsync(connection, "EXEC dbo.FailWith @Number = 0, @Message = N'custom failure', @Severity = 16, @State = 7");
            throw new Exception("RAISERROR did not raise");
        }
        catch (SqlException ex)
        {
            Equal(50000, ex.Number, "RAISERROR number");
            Equal((byte)16, ex.Class, "severity");
            Equal((byte)7, ex.State, "state");
            Equal("custom failure", ex.Message, "message");
            Equal("dbo.FailWith", ex.Procedure, "procedure name");
        }

        try
        {
            await ScalarAsync(connection, "THROW 51000, N'thrown', 3;");
            throw new Exception("THROW did not raise");
        }
        catch (SqlException ex)
        {
            Equal(51000, ex.Number, "THROW number");
            Equal((byte)3, ex.State, "THROW state");
        }

        Equal(1, (int)(await ScalarAsync(connection, "SELECT 1"))!, "connection still usable after errors");
    }

    private static async Task MultipleResults()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("SELECT 1; SELECT 'two', 2; SELECT Id FROM dbo.Customers WHERE Id < 0; SELECT 4", connection);
        await using var reader = await command.ExecuteReaderAsync();
        var shapes = new List<string>();
        do
        {
            int rows = 0;
            while (await reader.ReadAsync()) rows++;
            shapes.Add($"{reader.FieldCount}x{rows}");
        }
        while (await reader.NextResultAsync());
        Equal("1x1,2x1,1x0,1x1", string.Join(",", shapes), "result set shapes");
    }

    private static async Task Transactions()
    {
        string tag = "tx-" + Guid.NewGuid().ToString("N")[..8];
        await using var connection = await OpenAsync();

        await using (var transaction = connection.BeginTransaction(IsolationLevel.ReadCommitted))
        {
            await using var insert = new SqlCommand("INSERT dbo.Customers (Name) VALUES (@n)", connection, transaction);
            insert.Parameters.AddWithValue("@n", tag + "-rolledback");
            await insert.ExecuteNonQueryAsync();
            Equal(1, (int)(await new SqlCommand("SELECT @@TRANCOUNT", connection, transaction).ExecuteScalarAsync())!, "@@TRANCOUNT inside");
            transaction.Rollback();
        }

        await using (var transaction = connection.BeginTransaction())
        {
            await using var insert = new SqlCommand("INSERT dbo.Customers (Name) VALUES (@n)", connection, transaction);
            insert.Parameters.AddWithValue("@n", tag + "-kept");
            await insert.ExecuteNonQueryAsync();

            transaction.Save("half");
            await using var second = new SqlCommand("INSERT dbo.Customers (Name) VALUES (@n)", connection, transaction);
            second.Parameters.AddWithValue("@n", tag + "-savepoint");
            await second.ExecuteNonQueryAsync();
            transaction.Rollback("half");

            transaction.Commit();
        }

        await using var check = new SqlCommand("SELECT STRING_AGG(Name, ',') WITHIN GROUP (ORDER BY Name) FROM dbo.Customers WHERE Name LIKE @p", connection);
        check.Parameters.AddWithValue("@p", tag + "%");
        Equal(tag + "-kept", (string?)await check.ExecuteScalarAsync(), "only the committed, pre-savepoint row survives");
        Equal(0, (int)(await ScalarAsync(connection, "SELECT @@TRANCOUNT"))!, "@@TRANCOUNT after");
    }

    private static async Task PoolingReset()
    {
        var builder = new SqlConnectionStringBuilder(_proxy) { Pooling = true, MaxPoolSize = 1, ApplicationName = "TeeProbe-pool-" + Guid.NewGuid().ToString("N")[..6] };
        short firstSpid;

        await using (var connection = await OpenAsync(builder.ConnectionString))
        {
            firstSpid = (short)(await ScalarAsync(connection, "CREATE TABLE #leftover (x int); SET LANGUAGE Deutsch; SELECT @@SPID"))!;
        }

        await using (var connection = await OpenAsync(builder.ConnectionString))
        {
            short spid = (short)(await ScalarAsync(connection, "SELECT @@SPID"))!;
            Equal(firstSpid, spid, "the pooled physical connection (and its upstream session) was reused");
            Equal(null, await ScalarAsync(connection, "SELECT OBJECT_ID('tempdb..#leftover')"), "temp table dropped by the reset");
            Equal("us_english", (string?)await ScalarAsync(connection, "SELECT @@LANGUAGE"), "language restored by the reset");
        }

        SqlConnection.ClearPool(new SqlConnection(builder.ConnectionString));
    }

    private static async Task Cancel()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("WAITFOR DELAY '00:00:20'; SELECT 1", connection);

        var stopwatch = Stopwatch.StartNew();
        var running = command.ExecuteScalarAsync();
        await Task.Delay(500);
        command.Cancel();

        try
        {
            await running;
            throw new Exception("the command was not cancelled");
        }
        catch (SqlException ex) when (ex.Message.Contains("cancel", StringComparison.OrdinalIgnoreCase))
        {
        }

        Assert(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"cancel took {stopwatch.Elapsed}");
        Equal(7, (int)(await ScalarAsync(connection, "SELECT 7"))!, "connection usable after an attention");
    }

    private static async Task CommandTimeout()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("WAITFOR DELAY '00:00:10'", connection) { CommandTimeout = 1 };
        try
        {
            await command.ExecuteNonQueryAsync();
            throw new Exception("no timeout");
        }
        catch (SqlException ex) when (ex.Number == -2)
        {
        }

        Equal(8, (int)(await ScalarAsync(connection, "SELECT 8"))!, "connection usable after a timeout");
    }

    private static async Task BulkCopy()
    {
        const int rows = 100_000;
        var table = new DataTable();
        table.Columns.Add("Id", typeof(int));
        table.Columns.Add("Label", typeof(string));
        table.Columns.Add("Amount", typeof(decimal));
        table.Columns.Add("Stamp", typeof(DateTime));
        for (int i = 0; i < rows; i++) table.Rows.Add(i, $"row {i}", i * 1.5m, new DateTime(2026, 1, 1).AddSeconds(i));

        await using var connection = await OpenAsync();
        await ScalarAsync(connection, "TRUNCATE TABLE dbo.BulkTarget");

        using (var bulk = new SqlBulkCopy(connection) { DestinationTableName = "dbo.BulkTarget", BatchSize = 20_000 })
            await bulk.WriteToServerAsync(table);

        Equal(rows, (int)(await ScalarAsync(connection, "SELECT COUNT(*) FROM dbo.BulkTarget"))!, "rows bulk copied");
        Equal(rows * 1.5m * (rows - 1) / 2, (decimal)(await ScalarAsync(connection, "SELECT SUM(Amount) FROM dbo.BulkTarget"))!, "sum of amounts");
    }

    private static async Task Mars()
    {
        var builder = new SqlConnectionStringBuilder(_proxy) { MultipleActiveResultSets = true };
        await using var connection = await OpenAsync(builder.ConnectionString);

        await using var outer = new SqlCommand("SELECT Id FROM dbo.Customers WHERE Id <= 3 ORDER BY Id", connection);
        await using var reader = await outer.ExecuteReaderAsync();
        var names = new List<string>();
        while (await reader.ReadAsync())
        {
            // A second command on the same connection while the first reader is open: MARS or bust.
            await using var inner = new SqlCommand("SELECT Name FROM dbo.Customers WHERE Id = @id", connection);
            inner.Parameters.AddWithValue("@id", reader.GetInt32(0));
            names.Add((string)(await inner.ExecuteScalarAsync())!);
        }

        Equal("Ada Lovelace,Grace Hopper,Edsger Dijkstra", string.Join(",", names), "interleaved MARS commands");
    }

    private static async Task UseDatabase()
    {
        await using var connection = await OpenAsync();
        await ScalarAsync(connection, "USE DaffyTeeOther");
        Equal("DaffyTeeOther", (string?)await ScalarAsync(connection, "SELECT DB_NAME()"), "DB_NAME() after USE");
        Equal("DaffyTeeOther", connection.Database, "client-side Database property follows the ENVCHANGE");
        connection.ChangeDatabase("DaffyTeeTest");
        Equal("DaffyTeeTest", (string?)await ScalarAsync(connection, "SELECT DB_NAME()"), "after ChangeDatabase");
    }

    private static async Task LargeResult()
    {
        const int rows = 1_000_000;
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand($"SELECT N, CAST(N AS varchar(12)) + 'abcdefghij' AS S FROM dbo.Numbers({rows})", connection);
        await using var reader = await command.ExecuteReaderAsync();
        long sum = 0, count = 0, chars = 0;
        while (await reader.ReadAsync())
        {
            sum += reader.GetInt32(0);
            chars += reader.GetString(1).Length;
            count++;
        }

        Equal((long)rows, count, "rows");
        Equal((long)rows * (rows + 1) / 2, sum, "sum");
        Assert(chars > rows * 10, "string column read");
    }

    private static async Task Prepared()
    {
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("SELECT Name FROM dbo.Customers WHERE Id = @id", connection);
        command.Parameters.Add("@id", SqlDbType.Int);
        command.Prepare();

        var names = new List<string>();
        foreach (int id in new[] { 1, 2, 1 })
        {
            command.Parameters["@id"].Value = id;
            names.Add((string)(await command.ExecuteScalarAsync())!);
        }

        Equal("Ada Lovelace,Grace Hopper,Ada Lovelace", string.Join(",", names), "prepared executions");
    }

    // ---- load ------------------------------------------------------------------------------------

    /// <summary>
    /// A steady stream of small parameterised queries for <paramref name="seconds"/>, reporting each
    /// second's latency: what an application feels while something else (a broker outage, a flood of
    /// events) happens to the tee.
    /// </summary>
    private static async Task Load(int seconds)
    {
        Console.WriteLine();
        Console.WriteLine($"load: {seconds}s of parameterised SELECTs through the proxy");
        await using var connection = await OpenAsync();
        await using var command = new SqlCommand("SELECT @i + 1", connection);
        var parameter = command.Parameters.Add("@i", SqlDbType.Int);

        long errors = 0, total = 0;
        for (int second = 1; second <= seconds; second++)
        {
            var samples = new List<double>();
            var window = Stopwatch.StartNew();
            while (window.ElapsedMilliseconds < 1000)
            {
                parameter.Value = (int)total;
                long start = Stopwatch.GetTimestamp();
                try { await command.ExecuteScalarAsync(); }
                catch (SqlException) { errors++; }
                samples.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
                total++;
            }
            samples.Sort();
            Console.WriteLine($"  t={second,3}s  {samples.Count,6} req  p50 {samples[samples.Count / 2],6:F0} us  p99 {samples[(int)(samples.Count * 0.99)],7:F0} us  max {samples[^1],8:F0} us  errors {errors}");
        }

        Console.WriteLine($"load done: {total} requests, {errors} errors");
        if (errors > 0) _failed++; else _passed++;
    }

    /// <summary>
    /// Many sessions at once, each opening its own connection and running a mix of statements for
    /// <paramref name="seconds"/>: a batch, a parameterised query, a procedure call and a small
    /// transaction. Reports throughput, latency and every error.
    /// </summary>
    private static async Task ParallelLoad(int seconds, int sessions)
    {
        Console.WriteLine();
        Console.WriteLine($"parallel load: {sessions} sessions for {seconds}s");
        long requests = 0, errors = 0;
        var latencies = new System.Collections.Concurrent.ConcurrentBag<double>();
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        var firstErrors = new System.Collections.Concurrent.ConcurrentQueue<string>();

        async Task Worker(int n)
        {
            try
            {
                await using var connection = await OpenAsync(new SqlConnectionStringBuilder(_proxy) { ApplicationName = $"TeeProbe-load-{n}" }.ConnectionString);
                var local = new List<double>();
                int i = 0;
                while (DateTime.UtcNow < deadline)
                {
                    long start = Stopwatch.GetTimestamp();
                    try
                    {
                        switch (i++ % 4)
                        {
                            case 0:
                                await ScalarAsync(connection, $"SELECT COUNT(*) FROM dbo.Customers WHERE Id > {n % 3}");
                                break;
                            case 1:
                            {
                                await using var command = new SqlCommand("SELECT Name FROM dbo.Customers WHERE Id = @id", connection);
                                command.Parameters.Add("@id", SqlDbType.Int).Value = 1 + i % 4;
                                await command.ExecuteScalarAsync();
                                break;
                            }
                            case 2:
                            {
                                await using var command = new SqlCommand("dbo.GetCustomer", connection) { CommandType = CommandType.StoredProcedure };
                                command.Parameters.Add("@Id", SqlDbType.Int).Value = 2;
                                command.Parameters.Add("@Name", SqlDbType.NVarChar, 100).Direction = ParameterDirection.Output;
                                await using (var reader = await command.ExecuteReaderAsync()) while (await reader.ReadAsync()) { }
                                break;
                            }
                            default:
                            {
                                await using var transaction = (SqlTransaction)await connection.BeginTransactionAsync();
                                await using var command = new SqlCommand("SELECT @@TRANCOUNT", connection, transaction);
                                await command.ExecuteScalarAsync();
                                await transaction.RollbackAsync();
                                break;
                            }
                        }
                        Interlocked.Increment(ref requests);
                    }
                    catch (Exception ex)
                    {
                        if (Interlocked.Increment(ref errors) <= 5) firstErrors.Enqueue($"session {n}: {ex.GetType().Name}: {ex.Message}");
                    }
                    local.Add(Stopwatch.GetElapsedTime(start).TotalMicroseconds);
                }
                foreach (double l in local) latencies.Add(l);
            }
            catch (Exception ex)
            {
                if (Interlocked.Increment(ref errors) <= 5) firstErrors.Enqueue($"session {n} could not connect: {ex.Message}");
            }
        }

        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(1, sessions).Select(n => Task.Run(() => Worker(n))));
        clock.Stop();

        var sorted = latencies.ToArray();
        Array.Sort(sorted);
        Console.WriteLine($"  {requests:N0} requests in {clock.Elapsed.TotalSeconds:F1}s = {requests / clock.Elapsed.TotalSeconds:N0}/s; " +
                          $"p50 {sorted[sorted.Length / 2]:F0} us, p99 {sorted[(int)(sorted.Length * 0.99)]:F0} us, max {sorted[^1]:F0} us; errors {errors}");
        foreach (string error in firstErrors) Console.WriteLine($"  {error}");
        Console.WriteLine($"parallel load done: {requests} requests, {errors} errors");
        if (errors > 0) _failed++; else _passed++;
    }

    /// <summary>
    /// Connection churn: every statement on a brand-new, unpooled connection - a full login through
    /// the proxy each time - from several workers at once. What a proxy meant to run for months must
    /// survive without its memory or handle count creeping up.
    /// </summary>
    private static async Task Churn(int seconds, int workers)
    {
        Console.WriteLine();
        Console.WriteLine($"churn: {workers} workers opening a fresh connection per statement for {seconds}s");
        long logins = 0, errors = 0;
        var deadline = DateTime.UtcNow.AddSeconds(seconds);

        async Task Worker()
        {
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    await using var connection = await OpenAsync();
                    await ScalarAsync(connection, "SELECT 1");
                    Interlocked.Increment(ref logins);
                }
                catch (Exception ex)
                {
                    if (Interlocked.Increment(ref errors) <= 3) Console.WriteLine($"  {ex.GetType().Name}: {ex.Message}");
                }
            }
        }

        var clock = Stopwatch.StartNew();
        await Task.WhenAll(Enumerable.Range(0, workers).Select(_ => Task.Run(Worker)));
        Console.WriteLine($"  {logins:N0} logins in {clock.Elapsed.TotalSeconds:F0}s ({logins / clock.Elapsed.TotalSeconds:N0}/s), {errors} errors");
        Console.WriteLine($"churn done: {logins} logins, {errors} errors");
        if (errors > 0) _failed++; else _passed++;
    }

    // ---- benchmark -------------------------------------------------------------------------------

    private static async Task Benchmark()
    {
        Console.WriteLine();
        Console.WriteLine("benchmark: proxied vs direct (same machine, both over TLS)");

        foreach (var (label, cs) in new[] { ("direct ", _direct), ("proxied", _proxy) })
        {
            await using var connection = await OpenAsync(cs);
            await using var command = new SqlCommand("SELECT 1", connection);
            for (int i = 0; i < 200; i++) await command.ExecuteScalarAsync();   // warm up

            var samples = new double[3000];
            for (int i = 0; i < samples.Length; i++)
            {
                long start = Stopwatch.GetTimestamp();
                await command.ExecuteScalarAsync();
                samples[i] = Stopwatch.GetElapsedTime(start).TotalMicroseconds;
            }
            Array.Sort(samples);

            var bulk = Stopwatch.StartNew();
            await using var big = new SqlCommand("SELECT N, REPLICATE('x', 100) FROM dbo.Numbers(1000000)", connection);
            await using (var reader = await big.ExecuteReaderAsync())
                while (await reader.ReadAsync()) { }
            bulk.Stop();

            var login = Stopwatch.StartNew();
            for (int i = 0; i < 20; i++) { await using var c = await OpenAsync(cs); }
            login.Stop();

            Console.WriteLine($"  {label}  SELECT 1: p50 {samples[samples.Length / 2],7:F0} us  p99 {samples[(int)(samples.Length * 0.99)],7:F0} us" +
                              $"   1M rows x 100B: {bulk.ElapsedMilliseconds,5} ms   open+login: {login.ElapsedMilliseconds / 20.0,6:F1} ms");
        }
    }
}
