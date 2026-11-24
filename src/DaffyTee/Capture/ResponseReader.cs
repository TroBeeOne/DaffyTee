using System.Buffers.Binary;
using DaffyTee.Tds;

namespace DaffyTee.Capture;

/// <summary>What a response said, beyond its data: the parts that matter for auditing.</summary>
internal sealed class ResponseDetail
{
    public List<ServerMessage> Errors { get; } = [];

    /// <summary>INFO tokens: PRINT output, "Changed database context", RAISERROR below severity 11.</summary>
    public List<ServerMessage> Messages { get; } = [];

    /// <summary>Every DONE that carried a count, in order: one per statement that counted rows.</summary>
    public List<ulong> RowCounts { get; } = [];

    /// <summary>Rows in each result set returned, in order.</summary>
    public List<long> ResultSetRows { get; } = [];

    public int? ReturnStatus { get; set; }
    public List<(string Name, object? Value)> OutputParameters { get; } = [];

    /// <summary>The handle an sp_prepare / sp_prepexec returned (its first output parameter).</summary>
    public int? OutputHandle { get; set; }

    /// <summary>The database the session is in after this request, if it changed.</summary>
    public string? Database { get; set; }

    /// <summary>Transaction boundaries the server reported: begin (with its descriptor), commit, rollback.</summary>
    public List<string> Transactions { get; } = [];

    public bool ResetAcknowledged { get; set; }

    /// <summary>Set when the walk had to stop early; everything before that point is still reported.</summary>
    public string? Problem { get; set; }
}

/// <summary>
/// Decodes a complete response token stream, kept whole because it was small (see
/// TeeSettings.ResponseCaptureBytes). It walks every token, rows included - rows have no length
/// prefix, so the only way past them is to read each value - but keeps none of the data: only errors,
/// messages, counts, return values and environment changes.
///
/// A token it cannot walk ends the walk, and whatever was found by then is still reported. It is
/// advisory in every sense: it runs on the worker, after the fact, and cannot affect the session.
/// </summary>
internal static class ResponseReader
{
    private const ushort EncryptedColumnFlag = 0x0800;
    private const int MaxMessages = 20;

    public static ResponseDetail Read(ReadOnlySpan<byte> payload, SessionSnapshot session)
    {
        var detail = new ResponseDetail();
        var cursor = new TdsCursor(payload);
        List<TypeInfo>? columns = null;
        long rows = 0;

        // Before TDS 7.2: 4-byte DONE row counts, 2-byte UserTypes, one-part text/ntext/image table names.
        bool pre72 = TdsConstants.IsPre72(session.TdsVersion);

        try
        {
            while (cursor.Remaining > 0)
            {
                byte token = cursor.ReadByte();
                switch (token)
                {
                    case TdsConstants.Token.ColMetadata:
                    {
                        if (columns is not null) detail.ResultSetRows.Add(rows);
                        var read = ReadColumns(ref cursor, session.ColumnEncryption, pre72);
                        if (read is null)
                        {
                            detail.Problem = "encrypted-column metadata (Always Encrypted) is not decoded";
                            return detail;
                        }
                        // 0xFFFF columns means "same as before", sent with NoMetaData; keep the old shape.
                        if (read.Count > 0 || columns is null) columns = read;
                        rows = 0;
                        break;
                    }

                    case TdsConstants.Token.Row:
                    case TdsConstants.Token.NbcRow:
                        if (columns is not { Count: > 0 })
                        {
                            // A server cursor's fetch (sp_cursorfetch) answers with COLMETADATA 0xFFFF:
                            // its rows take the shape sent with the cursor's open, in an earlier
                            // response. Without it a row cannot be walked, and reading its bytes as
                            // tokens could even turn up an error that was never sent.
                            detail.Problem = columns is null
                                ? "rows without column metadata"
                                : "rows of a server-cursor fetch; their metadata came with the cursor's open";
                            return detail;
                        }
                        SkipRow(ref cursor, columns, nullBitmap: token == TdsConstants.Token.NbcRow);
                        rows++;
                        break;

                    case TdsConstants.Token.Done:
                    case TdsConstants.Token.DoneProc:
                    case TdsConstants.Token.DoneInProc:
                    {
                        ushort status = cursor.ReadUInt16();
                        cursor.ReadUInt16();                      // curcmd
                        ulong count = pre72 ? cursor.ReadUInt32() : cursor.ReadUInt64();
                        if ((status & TdsConstants.DoneStatus.Count) != 0) detail.RowCounts.Add(count);
                        if (columns is not null && token != TdsConstants.Token.DoneProc)
                        {
                            detail.ResultSetRows.Add(rows);
                            columns = null;
                            rows = 0;
                        }
                        break;
                    }

                    case TdsConstants.Token.Error:
                        detail.Errors.Add(LoginResponse.ReadMessage(ref cursor));
                        break;

                    case TdsConstants.Token.Info:
                    {
                        var message = LoginResponse.ReadMessage(ref cursor);
                        if (detail.Messages.Count < MaxMessages) detail.Messages.Add(message);
                        break;
                    }

                    case TdsConstants.Token.EnvChange:
                        ReadEnvChange(ref cursor, detail);
                        break;

                    case TdsConstants.Token.ReturnStatus:
                        detail.ReturnStatus = cursor.ReadInt32();
                        break;

                    case TdsConstants.Token.ReturnValue:
                        if (!ReadReturnValue(ref cursor, detail, session.ColumnEncryption, pre72))
                        {
                            detail.Problem = "an encrypted output parameter (Always Encrypted) is not decoded";
                            return detail;
                        }
                        break;

                    case TdsConstants.Token.Order:
                    case TdsConstants.Token.TabName:
                    case TdsConstants.Token.ColInfo:
                        cursor.Skip(cursor.ReadUInt16());
                        break;

                    case TdsConstants.Token.SessionState:
                        cursor.Skip((int)cursor.ReadUInt32());
                        break;

                    case TdsConstants.Token.Offset:
                        cursor.Skip(4);
                        break;

                    default:
                        detail.Problem = $"stopped at token 0x{token:X2} ({TdsConstants.TokenName(token)})";
                        return detail;
                }
            }
        }
        catch (Exception ex) when (ex is TdsUnsupportedException or ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            detail.Problem = $"stopped part-way: {ex.Message}";
        }

        return detail;
    }

    /// <summary>
    /// COLMETADATA. With column encryption acknowledged at login a CekTable follows the count; an
    /// empty one (the usual case) is just its zero count. Null means "not decodable here".
    /// </summary>
    private static List<TypeInfo>? ReadColumns(ref TdsCursor cursor, bool columnEncryption, bool pre72)
    {
        ushort count = cursor.ReadUInt16();
        if (count == 0xFFFF) return [];

        if (columnEncryption && cursor.ReadUInt16() != 0) return null;

        var columns = new List<TypeInfo>(count);
        for (int i = 0; i < count; i++)
        {
            if (pre72) cursor.ReadUInt16(); else cursor.ReadUInt32();   // UserType
            ushort flags = cursor.ReadUInt16();
            var type = ReadColumnType(ref cursor, pre72);
            if (columnEncryption && (flags & EncryptedColumnFlag) != 0) return null;
            cursor.ReadBVarChar();                             // column name
            columns.Add(type);
        }
        return columns;
    }

    /// <summary>
    /// A column's TYPE_INFO, which differs from a parameter's in two places: text, ntext and image
    /// name their table, and a CLR UDT carries its size and assembly name. The table name has a part
    /// count from TDS 7.2 on; before, it is one US_VARCHAR.
    /// </summary>
    private static TypeInfo ReadColumnType(ref TdsCursor cursor, bool pre72 = false)
    {
        byte type = cursor.Rest[0];
        switch (type)
        {
            case 0x23 or 0x63 or 0x22:
            {
                var info = TypeInfoReader.ReadTypeInfo(ref cursor);
                int parts = pre72 ? 1 : cursor.ReadByte();
                for (int i = 0; i < parts; i++) cursor.ReadUsVarChar();
                return info;
            }

            case 0xF0:
            {
                cursor.ReadByte();
                cursor.ReadUInt16();                           // max byte size
                string database = cursor.ReadBVarChar();
                string schema = cursor.ReadBVarChar();
                string name = cursor.ReadBVarChar();
                cursor.ReadUsVarChar();                        // assembly-qualified name
                return new TypeInfo(0xF0, 0xFFFF) { UdtName = $"{database}.{schema}.{name}".Trim('.') };
            }

            default:
                return TypeInfoReader.ReadTypeInfo(ref cursor);
        }
    }

    private static void SkipRow(ref TdsCursor cursor, List<TypeInfo> columns, bool nullBitmap)
    {
        ReadOnlySpan<byte> bitmap = nullBitmap ? cursor.ReadBytes((columns.Count + 7) / 8) : default;

        for (int i = 0; i < columns.Count; i++)
        {
            if (nullBitmap && (bitmap[i / 8] & (1 << (i % 8))) != 0) continue;
            SkipValue(ref cursor, columns[i]);
        }
    }

    /// <summary>
    /// In a ROW, text/ntext/image values carry a text pointer and timestamp ahead of the data; every
    /// other type reads exactly as it does in a parameter.
    /// </summary>
    private static void SkipValue(ref TdsCursor cursor, TypeInfo type)
    {
        if (type.Type is 0x23 or 0x63 or 0x22)
        {
            int pointer = cursor.ReadByte();
            if (pointer == 0) return;                          // NULL
            cursor.Skip(pointer + 8);                          // text pointer, timestamp
            cursor.Skip((int)cursor.ReadUInt32());
            return;
        }

        TypeInfoReader.ReadValue(ref cursor, type);
    }

    private static bool ReadReturnValue(ref TdsCursor cursor, ResponseDetail detail, bool columnEncryption, bool pre72)
    {
        ushort ordinal = cursor.ReadUInt16();
        string name = cursor.ReadBVarChar();
        cursor.ReadByte();                                     // status
        if (pre72) cursor.ReadUInt16(); else cursor.ReadUInt32();   // UserType
        ushort flags = cursor.ReadUInt16();
        var type = ReadColumnType(ref cursor, pre72);
        if (columnEncryption && (flags & EncryptedColumnFlag) != 0) return false;

        object? value = type.Type is 0x23 or 0x63 or 0x22 ? null : TypeInfoReader.ReadValue(ref cursor, type);
        detail.OutputParameters.Add((name.Length > 0 ? name : $"#{ordinal}", value));

        if (detail.OutputHandle is null && ordinal == 0 && value is int handle) detail.OutputHandle = handle;
        return true;
    }

    private static void ReadEnvChange(ref TdsCursor cursor, ResponseDetail detail)
    {
        int end = cursor.ReadUInt16() + cursor.Position;
        byte type = cursor.ReadByte();

        switch (type)
        {
            case TdsConstants.EnvChangeType.Database:
                detail.Database = cursor.ReadBVarChar();
                break;

            case TdsConstants.EnvChangeType.BeginTransaction:
            {
                var descriptor = cursor.ReadBVarByte();
                detail.Transactions.Add(descriptor.Length == 8
                    ? $"begin 0x{BinaryPrimitives.ReadUInt64LittleEndian(descriptor):X16}"
                    : "begin");
                break;
            }

            case TdsConstants.EnvChangeType.CommitTransaction:
                detail.Transactions.Add("commit");
                break;

            case TdsConstants.EnvChangeType.RollbackTransaction:
                detail.Transactions.Add("rollback");
                break;

            case TdsConstants.EnvChangeType.ResetConnectionAck:
                detail.ResetAcknowledged = true;
                break;
        }

        cursor.Seek(end);
    }
}
