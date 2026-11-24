using System.Buffers.Binary;
using DaffyTee.Capture;
using DaffyTee.Proxy;
using DaffyTee.Tds;

namespace DaffyTee.Tests;

/// <summary>
/// TDS 7.1, as the SQLSRV32 ODBC driver and the SQLOLEDB provider that ship with Windows still speak
/// it. The fixtures are real: the two LOGIN7s were captured from those drivers (Workstation ID WS42,
/// the ClientID - the sender's MAC address - zeroed), and the three responses are SQL Server 2022's
/// own answers to the ODBC driver: a refused login, a login, and SELECT @@VERSION.
/// </summary>
public class LegacyTdsTests
{
    private const uint Tds71 = 0x71000001;

    private static byte[] Fixture(string name) => RequestDecodingTests.Fixture(name);

    [Fact]
    public void WindowsOdbcLoginIsReadAsTds71()
    {
        var login = Login7.Parse(Fixture("login7-tds71-odbc"));

        Assert.Equal("7.1", login.TdsVersionName);
        Assert.Equal(86, login.HeaderLength);
        Assert.Equal("WS42", login.HostName);
        Assert.Equal("legacy", login.UserName);
        Assert.Equal("legacy-pass", login.Password);
        Assert.Equal("LegacyApp", login.AppName);
        Assert.Equal("Sales", login.Database);
        Assert.Equal("ODBC", login.ClientInterfaceName);
        Assert.Equal(0u, login.PacketSize);
        Assert.Empty(login.FeatureIds);

        // 0.3.1 read bytes 86-93 - the start of "WS42" - as a change-password entry and refused the login.
        Assert.Equal("", login.ChangePassword);
        Assert.False(login.RequestsPasswordChange);
        // The driver sends 0x08 in what is a reserved byte before TDS 7.2.
        Assert.Equal(0x08, login.OptionFlags3);
    }

    [Fact]
    public void WindowsOleDbLoginIsReadAsTds71()
    {
        var login = Login7.Parse(Fixture("login7-tds71-oledb"));

        Assert.Equal("7.1", login.TdsVersionName);
        Assert.Equal("OLEDB", login.ClientInterfaceName);
        Assert.Equal(4096u, login.PacketSize);
        Assert.Equal(0x10, login.TypeFlags);                 // fOLEDB
        Assert.False(login.RequestsPasswordChange);
        Assert.Equal("legacy-pass", login.Password);
    }

    [Fact]
    public void Tds71LoginIsRebuiltWithItsOwnFixedPart()
    {
        var original = Login7.Parse(Fixture("login7-tds71-odbc"));
        byte[] built = original.Build();

        Assert.Equal(86, BinaryPrimitives.ReadUInt16LittleEndian(built.AsSpan(36)));   // ibHostName: right after the fixed part
        Assert.Equal(built.Length, (int)BinaryPrimitives.ReadUInt32LittleEndian(built));

        var again = Login7.Parse(built);
        Assert.Equal(original.TdsVersion, again.TdsVersion);
        Assert.Equal(original.HostName, again.HostName);
        Assert.Equal(original.Password, again.Password);
        Assert.Equal(original.ClientInterfaceName, again.ClientInterfaceName);
        Assert.Equal(original.Database, again.Database);
        Assert.Equal(original.OptionFlags3, again.OptionFlags3);
        Assert.Equal(Convert.ToHexString(built), Convert.ToHexString(again.Build()));
    }

    [Fact]
    public void Tds71LoginIsRewrittenForTheTargetAsTds71()
    {
        var client = Login7.Parse(Fixture("login7-tds71-odbc"));
        var target = TargetEndpoint.Parse("Server=tcp:sql01,1433;User Id=svc_app;Password=Svc!App2026;Initial Catalog=Other");

        var rewritten = Login7.Parse(UpstreamConnector.ForTarget(client, target, client.Database).Build());

        Assert.Equal(Tds71, rewritten.TdsVersion);
        Assert.Equal("svc_app", rewritten.UserName);
        Assert.Equal("Svc!App2026", rewritten.Password);
        Assert.Equal("Sales", rewritten.Database);
        Assert.Equal("ODBC", rewritten.ClientInterfaceName);
        Assert.Null(rewritten.FeatureExt);
        Assert.False(rewritten.RequestsPasswordChange);
    }

    [Fact]
    public void Tds71LoginCannotCarryFeatures()
    {
        var login = Login7.Parse(Fixture("login7-tds71-odbc"));
        login.AddFeature(TdsConstants.FeatureId.FedAuth, [0x05, 0x01]);
        Assert.Throws<InvalidOperationException>(() => login.Build());
    }

    [Fact]
    public void PasswordChangeIsStillRecognisedFromTds72On()
    {
        var withEntry = new Login7 { TdsVersion = 0x74000004, UserName = "u", Password = "p", ChangePassword = "new" };
        Assert.True(Login7.Parse(withEntry.Build()).RequestsPasswordChange);

        var withFlag = new Login7 { TdsVersion = 0x74000004, UserName = "u", Password = "p", OptionFlags3 = 0x01 };
        Assert.True(Login7.Parse(withFlag.Build()).RequestsPasswordChange);

        // The same bit means nothing in TDS 7.1, where the byte is reserved.
        var old = new Login7 { TdsVersion = Tds71, UserName = "u", Password = "p", OptionFlags3 = 0x01 };
        Assert.False(Login7.Parse(old.Build()).RequestsPasswordChange);
    }

    [Fact]
    public void LoginSummaryShowsInterfaceAndOptionFlags()
    {
        string summary = Login7.Parse(Fixture("login7-tds71-odbc")).ToString();
        Assert.Contains("tds=7.1", summary);
        Assert.Contains("interface='ODBC'", summary);
        Assert.Contains("flags=E0-03-00-08", summary);
        Assert.DoesNotContain("legacy-pass", summary);
    }

    [Fact]
    public void Tds71LoginAckIsRead()
    {
        var response = LoginResponse.Parse(Fixture("tds71-loginack"), Tds71);

        Assert.True(response.Succeeded);
        Assert.Equal(Tds71, response.TdsVersion);
        Assert.Equal("7.1", response.TdsVersionName);
        Assert.Equal("DaffyTeeTest", response.Database);
        Assert.Equal(4096, response.PacketSize);
    }

    [Fact]
    public void Tds71LoginRefusalIsRead()
    {
        var response = LoginResponse.Parse(Fixture("tds71-login-refused"), Tds71);

        Assert.False(response.Succeeded);
        Assert.Equal(ServerMessages.LoginFailed, Assert.Single(response.Errors).Number);
    }

    /// <summary>
    /// DaffyTee's own refusal, for a TDS 7.1 client, has SQL Server's shape: a 2-byte line number and
    /// an 8-byte DONE. With the 7.2 shape the Windows drivers reported "SQL Server version 6.5 and all
    /// previous versions are no longer supported" instead of the refusal.
    /// </summary>
    [Fact]
    public void RefusalForATds71ClientHasSqlServersShape()
    {
        const string text = "Login failed for user 'legacy'.";
        byte[] ours = ServerMessages.Error(ServerMessages.LoginFailed, 14, text, tdsVersion: Tds71);
        byte[] theirs = Fixture("tds71-login-refused");

        // ERROR: number(4) state(1) class(1) text(US_VARCHAR) server(B_VARCHAR) proc(B_VARCHAR) line(2).
        int body = BinaryPrimitives.ReadUInt16LittleEndian(ours.AsSpan(1));
        int expected = 4 + 1 + 1 + 2 + 2 * text.Length + 1 + 2 * ServerMessages.ServerName.Length + 1 + 2;
        Assert.Equal(expected, body);

        // DONE(ERROR), 8 bytes after its token, exactly as SQL Server ends its own refusal.
        Assert.Equal(Convert.ToHexString(theirs[^9..]), Convert.ToHexString(ours[^9..]));
        Assert.Equal(3 + body + 9, ours.Length);

        var read = LoginResponse.Parse(ours, Tds71);
        Assert.Equal(text, Assert.Single(read.Errors).Text);
    }

    [Fact]
    public void RefusalForCurrentClientsIsUnchanged()
    {
        byte[] ours = ServerMessages.Error(ServerMessages.LoginFailed, 14, "x");
        Assert.Equal(0xFD, ours[^13]);                       // DONE with an 8-byte row count
        Assert.Equal(ServerMessages.LoginFailed, Assert.Single(LoginResponse.Parse(ours).Errors).Number);
    }

    [Fact]
    public void Tds71ResultIsDecoded()
    {
        var session = new SessionSnapshot(1, Guid.Empty, "legacy", "svc_app", "t:1", 51, "db", "h", "a", "c", "", "", "",
                                          ColumnEncryption: false, TdsVersion: Tds71);

        var detail = ResponseReader.Read(Fixture("tds71-select-version"), session);

        Assert.Null(detail.Problem);
        Assert.Equal([1L], detail.ResultSetRows);
        Assert.Equal([1UL], detail.RowCounts);
    }

    /// <summary>
    /// The relay keeps a response's last bytes for its final DONE - status, attention, row count. A
    /// TDS 7.1 DONE is 9 bytes, not 13; read as 13, 0.3.1 lost the row count and would miss a cancel.
    /// </summary>
    [Fact]
    public void Tds71FinalDoneIsReadFromTheTail()
    {
        byte[] payload = Fixture("tds71-select-version");
        var exchange = new CapturedExchange
        {
            Session = new SessionSnapshot(1, Guid.Empty, "legacy", "svc_app", "t:1", 51, "db", "h", "a", "c", "", "", "", false, TdsVersion: Tds71),
            Seq = 1, Type = TdsConstants.PacketType.SqlBatch, ResetConnection = false,
            StartUtc = DateTime.UtcNow, StartTimestamp = 0, Emit = false,
        };

        exchange.ReadFinalDone(payload[^13..], Tds71);

        Assert.Equal(TdsConstants.Token.Done, exchange.DoneToken);
        Assert.Equal(TdsConstants.DoneStatus.Count, exchange.DoneStatus);
        Assert.Equal(1UL, exchange.DoneRowCount);
    }

    [Fact]
    public void Tds71ResultIsAnnotated()
    {
        byte[] payload = Fixture("tds71-select-version");
        var annotator = new Annotator(columnEncryptionAcknowledged: false, tdsVersion: Tds71);

        string text = annotator.Describe(new TdsMessage(TdsConstants.PacketType.TabularResult, payload, 1, 0x01));

        Assert.DoesNotContain("decode failed", text);
        Assert.Contains("userType=0", text);
        Assert.Contains("rows=1", text);
        Assert.Contains("Microsoft SQL Server", text);
    }

    [Fact]
    public void JdbcUserAgentFeatureHasAName()
    {
        Assert.Equal("USERAGENT", TdsConstants.FeatureId.Name(0x10));
    }

    [Fact]
    public void Tds71VersionsAreRecognisedInBothEncodings()
    {
        Assert.True(TdsConstants.IsPre72(0x71000001));
        Assert.True(TdsConstants.IsPre72(0x07010000));       // LOGINACK's spelling of 7.1
        Assert.True(TdsConstants.IsPre72(0x70000000));
        Assert.False(TdsConstants.IsPre72(0x72090002));
        Assert.False(TdsConstants.IsPre72(0x74000004));
        Assert.False(TdsConstants.IsPre72(TdsConstants.TdsVersion80));
        Assert.False(TdsConstants.IsPre72(0));               // not known yet: read as current
        Assert.Equal(8, TdsConstants.DoneLength(0x71000001));
        Assert.Equal(12, TdsConstants.DoneLength(0x74000004));
    }
}
