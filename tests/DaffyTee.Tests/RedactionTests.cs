using DaffyTee.Capture;
using DaffyTee.Tds;

namespace DaffyTee.Tests;

/// <summary>Passwords and secrets written into SQL are masked before an event goes anywhere.</summary>
public class RedactionTests
{
    [Theory]
    [InlineData("CREATE LOGIN bob WITH PASSWORD = 'Sup3r!Secret' MUST_CHANGE, CHECK_EXPIRATION = ON",
                "CREATE LOGIN bob WITH PASSWORD = '***' MUST_CHANGE, CHECK_EXPIRATION = ON")]
    [InlineData("ALTER LOGIN bob WITH PASSWORD = N'new1' OLD_PASSWORD = N'old1'",
                "ALTER LOGIN bob WITH PASSWORD = N'***' OLD_PASSWORD = N'***'")]
    [InlineData("create login bob with password=0x0200AABBCC hashed",
                "create login bob with password='***' hashed")]
    [InlineData("CREATE CREDENTIAL c WITH IDENTITY = 'SHARED ACCESS SIGNATURE', SECRET = 'sv=2026&sig=abc'",
                "CREATE CREDENTIAL c WITH IDENTITY = 'SHARED ACCESS SIGNATURE', SECRET = '***'")]
    [InlineData("CREATE USER app WITH PASSWORD = 'x''y'", "CREATE USER app WITH PASSWORD = '***'")]
    [InlineData("CREATE MASTER KEY ENCRYPTION BY PASSWORD = 'mk'; BACKUP DATABASE d TO DISK = 'd.bak' WITH MEDIAPASSWORD = 'mp'",
                "CREATE MASTER KEY ENCRYPTION BY PASSWORD = '***'; BACKUP DATABASE d TO DISK = 'd.bak' WITH MEDIAPASSWORD = '***'")]
    [InlineData("CREATE SYMMETRIC KEY k WITH ALGORITHM = AES_256, KEY_SOURCE = 'phrase', IDENTITY_VALUE = 'id' ENCRYPTION BY PASSWORD = 'p'",
                "CREATE SYMMETRIC KEY k WITH ALGORITHM = AES_256, KEY_SOURCE = '***', IDENTITY_VALUE = 'id' ENCRYPTION BY PASSWORD = '***'")]
    [InlineData("CREATE CERTIFICATE c FROM BINARY = 0x3082 WITH PRIVATE KEY (BINARY = 0x1234, DECRYPTION BY PASSWORD = 'p')",
                "CREATE CERTIFICATE c FROM BINARY = 0x3082 WITH PRIVATE KEY (BINARY = '***', DECRYPTION BY PASSWORD = '***')")]
    [InlineData("UPDATE dbo.Users SET PasswordHash = 0xABCD WHERE Id = 7 AND Name = 'bob'",
                "UPDATE dbo.Users SET PasswordHash = '***' WHERE Id = 7 AND Name = 'bob'")]
    public void PasswordStatementsAreMasked(string sql, string expected) =>
        Assert.Equal(expected, SecretRedactor.RedactSql(sql));

    [Theory]
    [InlineData("EXEC sp_addlogin 'bob', 'pw1', 'Sales'", "EXEC sp_addlogin 'bob', '***', 'Sales'")]
    [InlineData("EXEC master.dbo.sp_password 'old1', 'new1', 'bob'", "EXEC master.dbo.sp_password '***', '***', 'bob'")]
    [InlineData("EXEC sp_password @old = NULL, @new = 'n', @loginame = 'bob'", "EXEC sp_password @old = NULL, @new = '***', @loginame = 'bob'")]
    [InlineData("EXEC sp_addlinkedsrvlogin 'SRV', 'false', NULL, 'remote', 'rpw'",
                "EXEC sp_addlinkedsrvlogin 'SRV', 'false', NULL, 'remote', '***'")]
    [InlineData("EXEC sp_addlinkedsrvlogin @rmtsrvname = N'SRV', @useself = N'False', @rmtuser = N'remote', @rmtpassword = N'rpw'",
                "EXEC sp_addlinkedsrvlogin @rmtsrvname = N'SRV', @useself = N'False', @rmtuser = N'remote', @rmtpassword = N'***'")]
    [InlineData("EXEC sp_setapprole 'r', {Encrypt N'apw'}, 'odbc'", "EXEC sp_setapprole 'r', {Encrypt N'***'}, 'odbc'")]
    public void SystemProceduresThatTakeAPassword(string sql, string expected) =>
        Assert.Equal(expected, SecretRedactor.RedactSql(sql));

    [Theory]
    [InlineData("SELECT ENCRYPTBYPASSPHRASE('phrase', 'text'), DECRYPTBYPASSPHRASE(N'phrase', @c)",
                "SELECT ENCRYPTBYPASSPHRASE('***', 'text'), DECRYPTBYPASSPHRASE(N'***', @c)")]
    [InlineData("SELECT DECRYPTBYKEYAUTOCERT(CERT_ID('c'), N'cpw', @cipher)",
                "SELECT DECRYPTBYKEYAUTOCERT(CERT_ID('c'), N'***', @cipher)")]
    [InlineData("SELECT name FROM sys.sql_logins WHERE PWDCOMPARE('guess', password_hash) = 1",
                "SELECT name FROM sys.sql_logins WHERE PWDCOMPARE('***', password_hash) = 1")]
    public void FunctionsThatTakeASecret(string sql, string expected) =>
        Assert.Equal(expected, SecretRedactor.RedactSql(sql));

    [Fact]
    public void AVariableCarryingThePasswordIsMaskedWhereItIsSet()
    {
        const string sql = """
            DECLARE @login sysname = N'bob', @pw nvarchar(128) = N'Sup3r!Secret';
            SET @pw = @pw + N'2';
            CREATE LOGIN [bob] WITH PASSWORD = @pw;
            """;

        string masked = SecretRedactor.RedactSql(sql);

        Assert.DoesNotContain("Sup3r", masked);
        Assert.Contains("@login sysname = N'bob'", masked);
        Assert.Contains("@pw nvarchar(128) = N'***'", masked);
        Assert.Contains("SET @pw = @pw + N'***'", masked);
    }

    [Fact]
    public void DynamicSqlIsWalkedToo()
    {
        Assert.Equal("EXEC('CREATE LOGIN bob WITH PASSWORD = ''***''')",
                     SecretRedactor.RedactSql("EXEC('CREATE LOGIN bob WITH PASSWORD = ''Sup3r!Secret''')"));

        Assert.Equal("EXEC sp_executesql N'CREATE LOGIN bob WITH PASSWORD = @p', N'@p nvarchar(50)', @p = N'***'",
                     SecretRedactor.RedactSql("EXEC sp_executesql N'CREATE LOGIN bob WITH PASSWORD = @p', N'@p nvarchar(50)', @p = N'Sup3r!Secret'"));
    }

    [Fact]
    public void APasswordConcatenatedIntoDynamicSqlIsMaskedWhereItIsSet()
    {
        const string sql = """
            DECLARE @x nvarchar(50) = N'Sup3r!Secret';
            DECLARE @sql nvarchar(max) = N'CREATE LOGIN ' + QUOTENAME(@login) + N' WITH PASSWORD = ''' + @x + N'''';
            EXEC (@sql);
            """;

        string masked = SecretRedactor.RedactSql(sql);

        Assert.DoesNotContain("Sup3r", masked);
        Assert.Contains("N' WITH PASSWORD = ''' + @x", masked);
    }

    [Fact]
    public void ConnectionStringPasswordsAreMasked()
    {
        Assert.Equal("SELECT * FROM OPENDATASOURCE('MSOLEDBSQL', 'Data Source=srv;User ID=sa;Password=***;').db.dbo.t",
                     SecretRedactor.RedactSql("SELECT * FROM OPENDATASOURCE('MSOLEDBSQL', 'Data Source=srv;User ID=sa;Password=p4ss;').db.dbo.t"));

        Assert.Equal("SELECT * FROM OPENROWSET('SQLNCLI', 'srv'; 'sa'; '***', 'SELECT 1')",
                     SecretRedactor.RedactSql("SELECT * FROM OPENROWSET('SQLNCLI', 'srv'; 'sa'; 'p4ss', 'SELECT 1')"));

        Assert.Equal("Server=x;Pwd=***;Encrypt=True", SecretRedactor.MaskConnectionString("Server=x;Pwd={a;b};Encrypt=True"));
    }

    [Theory]
    [InlineData("SELECT * FROM dbo.Orders WHERE CustomerId = @id AND Status = 'open'")]
    [InlineData("-- reset the password later\nSELECT 'password reset' AS note, is_expired FROM sys.sql_logins")]
    [InlineData("SELECT name FROM sys.sql_logins WHERE is_expiration_checked = 1 AND password_hash IS NOT NULL")]
    public void OrdinarySqlIsLeftAlone(string sql) =>
        Assert.Same(sql, SecretRedactor.RedactSql(sql));

    [Fact]
    public void RpcParametersInASecretsPlaceAreMasked()
    {
        var nvarchar = new TypeInfo(0xE7, 100);
        var calls = (IReadOnlyList<RpcCall>?)new List<RpcCall>
        {
            new("sp_executesql", TdsConstants.ProcId.ExecuteSql, "ALTER LOGIN bob WITH PASSWORD = @p1", "@p1 nvarchar(50)", null,
                [new RpcParameter("@p1", nvarchar, "nvarchar(50)", 0, "Sup3r!Secret")]),
            new("dbo.ChangePassword", 0, null, null, null,
                [new RpcParameter("@UserId", new TypeInfo(0x26, 4), "int", 0, 7),
                 new RpcParameter("@NewPassword", nvarchar, "nvarchar(50)", 0, "hunter2")]),
            new("dbo.GetOrders", 0, null, null, null,
                [new RpcParameter("@CustomerId", new TypeInfo(0x26, 4), "int", 0, 42)]),
        };
        string? sql = null;
        var masked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        Assert.True(SecretRedactor.Redact(ref sql, ref calls, masked));

        Assert.Equal("***", calls![0].Parameters[0].Value);
        Assert.Equal("ALTER LOGIN bob WITH PASSWORD = @p1", calls[0].Sql);
        Assert.Equal(7, calls[1].Parameters[0].Value);
        Assert.Equal("***", calls[1].Parameters[1].Value);
        Assert.Equal(42, calls[2].Parameters[0].Value);
        Assert.Contains("@NewPassword", masked);

        var detail = new ResponseDetail();
        detail.OutputParameters.Add(("@NewPassword", "hunter2"));
        detail.OutputParameters.Add(("@Rows", 3));
        Assert.True(SecretRedactor.RedactOutputs(detail, masked));
        Assert.Equal(("@NewPassword", (object?)"***"), detail.OutputParameters[0]);
        Assert.Equal(("@Rows", (object?)3), detail.OutputParameters[1]);
    }

    [Fact]
    public void MaskedEventsSaySo()
    {
        var e = new AuditEvent
        {
            Type = "request.batch",
            TimeUtc = DateTime.UtcNow,
            Request = new RequestPart { Kind = "batch", Sql = SecretRedactor.RedactSql("CREATE LOGIN a WITH PASSWORD = 'x'"), Redacted = true },
        };

        Assert.Contains("-- (passwords and secrets masked as ***)", SqlText.Render(e));
        Assert.Contains("\"redacted\":true", System.Text.Encoding.UTF8.GetString(EventJson.Serialize(e, "test")));
    }
}
