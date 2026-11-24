-- Seed for the DaffyTee test SQL Server. Idempotent: safe to run again.
--
--   docker exec daffytee-sql-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "DaffyTee!Dev2026" -i /seed/seed.sql
--
-- Two SQL logins, standing in for the two shapes a mapping takes:
--   svc_app   a service account several DaffyTee users could share
--   app_user  a person's own login on the target, with a different password from their DaffyTee one

SET NOCOUNT ON;

IF DB_ID('DaffyTeeTest') IS NULL CREATE DATABASE DaffyTeeTest;
GO
IF DB_ID('DaffyTeeOther') IS NULL CREATE DATABASE DaffyTeeOther;
GO

IF SUSER_ID('svc_app') IS NULL
    CREATE LOGIN svc_app WITH PASSWORD = 'Svc!App2026', CHECK_POLICY = OFF, DEFAULT_DATABASE = DaffyTeeTest;
IF SUSER_ID('app_user') IS NULL
    CREATE LOGIN app_user WITH PASSWORD = 'App!User2026', CHECK_POLICY = OFF, DEFAULT_DATABASE = DaffyTeeTest;
GO

USE DaffyTeeTest;
GO

IF USER_ID('svc_app') IS NULL CREATE USER svc_app FOR LOGIN svc_app;
IF USER_ID('app_user') IS NULL CREATE USER app_user FOR LOGIN app_user;
ALTER ROLE db_owner ADD MEMBER svc_app;
ALTER ROLE db_datareader ADD MEMBER app_user;
ALTER ROLE db_datawriter ADD MEMBER app_user;
GRANT EXECUTE TO app_user;
GO

IF OBJECT_ID('dbo.Customers') IS NULL
CREATE TABLE dbo.Customers
(
    Id        int IDENTITY(1,1) PRIMARY KEY,
    Name      nvarchar(100)    NOT NULL,
    Email     varchar(200)     NULL,
    Balance   decimal(18,2)    NOT NULL DEFAULT 0,
    CreatedAt datetime2(7)     NOT NULL DEFAULT SYSUTCDATETIME(),
    Notes     nvarchar(max)    NULL,
    Photo     varbinary(max)   NULL,
    RowGuid   uniqueidentifier NOT NULL DEFAULT NEWID()
);
GO

IF OBJECT_ID('dbo.Orders') IS NULL
CREATE TABLE dbo.Orders
(
    Id         int IDENTITY(1,1) PRIMARY KEY,
    CustomerId int           NOT NULL REFERENCES dbo.Customers(Id),
    Amount     money         NOT NULL,
    OrderedOn  date          NOT NULL,
    Shipped    bit           NOT NULL DEFAULT 0
);
GO

-- A wide, empty target for bulk copy tests.
IF OBJECT_ID('dbo.BulkTarget') IS NULL
CREATE TABLE dbo.BulkTarget
(
    Id      int           NOT NULL,
    Label   nvarchar(50)  NOT NULL,
    Amount  decimal(18,4) NULL,
    Stamp   datetime2(3)  NOT NULL
);
GO

IF NOT EXISTS (SELECT 1 FROM dbo.Customers)
BEGIN
    INSERT dbo.Customers (Name, Email, Balance)
    VALUES (N'Ada Lovelace', 'ada@example.com', 120.50),
           (N'Grace Hopper', 'grace@example.com', 99.99),
           (N'Edsger Dijkstra', NULL, 0),
           (N'Zoë Ünïcode', 'zoe@example.com', -5.25);

    INSERT dbo.Orders (CustomerId, Amount, OrderedOn, Shipped)
    VALUES (1, 10.00, '2026-01-02', 1), (1, 25.50, '2026-02-03', 0), (2, 7.25, '2026-03-04', 1);
END
GO

IF TYPE_ID('dbo.IdList') IS NULL
    CREATE TYPE dbo.IdList AS TABLE (Id int NOT NULL PRIMARY KEY);
GO

CREATE OR ALTER PROCEDURE dbo.GetCustomer
    @Id int,
    @Name nvarchar(100) OUTPUT
AS
BEGIN
    SET NOCOUNT ON;
    SELECT @Name = Name FROM dbo.Customers WHERE Id = @Id;
    SELECT Id, Name, Email, Balance FROM dbo.Customers WHERE Id = @Id;
    RETURN CASE WHEN @Name IS NULL THEN 1 ELSE 0 END;
END
GO

CREATE OR ALTER PROCEDURE dbo.GetCustomersByIds
    @Ids dbo.IdList READONLY
AS
BEGIN
    SET NOCOUNT ON;
    SELECT c.Id, c.Name FROM dbo.Customers c JOIN @Ids i ON i.Id = c.Id ORDER BY c.Id;
END
GO

CREATE OR ALTER PROCEDURE dbo.FailWith
    @Number int, @Message nvarchar(200), @Severity int = 16, @State int = 1
AS
BEGIN
    RAISERROR(@Message, @Severity, @State);
END
GO

-- Rows on demand, for large-result tests without storing them.
CREATE OR ALTER FUNCTION dbo.Numbers(@Count int)
RETURNS TABLE
AS RETURN
    WITH e1(n) AS (SELECT 1 FROM (VALUES (1),(1),(1),(1),(1),(1),(1),(1),(1),(1)) v(n)),
         e3(n) AS (SELECT 1 FROM e1 a CROSS JOIN e1 b CROSS JOIN e1 c),
         e9(n) AS (SELECT 1 FROM e3 a CROSS JOIN e3 b CROSS JOIN e3 c)
    SELECT TOP (@Count) CAST(ROW_NUMBER() OVER (ORDER BY (SELECT NULL)) AS int) AS N FROM e9;
GO

USE DaffyTeeOther;
GO
IF USER_ID('svc_app') IS NULL CREATE USER svc_app FOR LOGIN svc_app;
ALTER ROLE db_owner ADD MEMBER svc_app;
GO

PRINT 'seeded';
