namespace DaffyTee.Tds;

/// <summary>What the target said in answer to one of DaffyTee's own small requests.</summary>
internal sealed class ScannedResponse
{
    /// <summary>Every ENVCHANGE token, raw and complete, with its type.</summary>
    public List<(byte Type, byte[] Token)> EnvChanges { get; } = [];

    /// <summary>INFO tokens - PRINT output among them - in order.</summary>
    public List<ServerMessage> Messages { get; } = [];

    public List<ServerMessage> Errors { get; } = [];

    /// <summary>Set when a token was met that this scanner does not walk; what came before it is kept.</summary>
    public string? Problem { get; set; }
}

/// <summary>
/// Reads the answer to a request DaffyTee sent on its own account: the reset of a pooled connection
/// that arrived on a daffytee.* command. (From DaffyDuo, which also sends PRINTs and USEs.) Those
/// answers hold only ENVCHANGE, INFO, ERROR, RETURNSTATUS and DONE tokens, so this walks just
/// those - result sets never come back from these requests.
/// </summary>
internal static class TokenScanner
{
    public static ScannedResponse Scan(ReadOnlySpan<byte> payload)
    {
        var response = new ScannedResponse();
        var cursor = new TdsCursor(payload);

        try
        {
            while (cursor.Remaining > 0)
            {
                int start = cursor.Position;
                byte token = cursor.ReadByte();
                switch (token)
                {
                    case TdsConstants.Token.EnvChange:
                    {
                        int length = cursor.ReadUInt16();
                        byte type = payload[cursor.Position];
                        cursor.Skip(length);
                        response.EnvChanges.Add((type, payload[start..cursor.Position].ToArray()));
                        break;
                    }

                    case TdsConstants.Token.Info:
                        response.Messages.Add(LoginResponse.ReadMessage(ref cursor));
                        break;

                    case TdsConstants.Token.Error:
                        response.Errors.Add(LoginResponse.ReadMessage(ref cursor));
                        break;

                    case TdsConstants.Token.ReturnStatus:
                        cursor.Skip(4);
                        break;

                    case TdsConstants.Token.Done:
                    case TdsConstants.Token.DoneProc:
                    case TdsConstants.Token.DoneInProc:
                        // status(2) curcmd(2) rowcount(8): daffytee.* commands, the only reason for these
                        // requests, are answered from TDS 7.3 on, so never the 4-byte count of 7.1.
                        cursor.Skip(12);
                        break;

                    default:
                        response.Problem = $"unexpected {TdsConstants.TokenName(token)} token at offset {start}";
                        return response;
                }
            }
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException or InvalidDataException)
        {
            response.Problem = $"the response ended part-way through a token ({ex.Message})";
        }

        return response;
    }
}
