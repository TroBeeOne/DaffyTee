# How clients start a session, as DaffyTee records it

What each SQL Server client library sends when it opens a session, and what DaffyTee writes down
about it. Every client ran exactly one statement, `SELECT @@VERSION`, with its own defaults. Anything else below was the client's own doing.

- [How this was captured](#how-this-was-captured) · [Where DaffyTee records a session's start](#where-daffytee-records-a-sessions-start)
- [Summary](#summary) · [The same for every client](#the-same-for-every-client) · [What differs](#what-differs)
- [Findings, and what was fixed](#findings-and-what-was-fixed) · [Each client, as captured](#each-client-as-captured)

## How this was captured

- **When and what:** 2026-11-15, on Windows 11. DaffyTee was a Debug build of 0.3.2: 0.3.1
  with the fixes under [Findings](#findings-and-what-was-fixed), which this document came with,
  run with default settings and every output on. The target was the test SQL Server from `docker/compose.yml`:
  SQL Server 2022 CU27 (16.0.4295.3, Developer, Linux container), mapped as `svc_app` with the
  default database `DaffyTeeTest`.
- **The script:** `scripts/capture-client-init.ps1`. Each client logs in as its own DaffyTee login,
  named after it (`odbc17`, `jdbc`, ...), so every record says which client made it. Each client
  sets only the server, the login, encryption, and trust in DaffyTee's self-signed certificate.
  Each runs in its own process, one after another.
- **JDBC** used DBeaver's bundled Java runtime (OpenJDK 25.0.3) and the `mssql-jdbc-13.4.0.jre11.jar`
  that DBeaver had already downloaded. The ten-line test was compiled with ECJ 3.46.100, a single
  jar from Maven Central. **tedious** 20.3.9 was installed with npm into a scratch folder.
- **Machine name:** replaced with `DEV01`, and the output folder's path with `<out>`. Nothing else
  was edited.

```powershell
# All the clients. JDBC and tedious are skipped unless their paths are given.
powershell -File scripts\capture-client-init.ps1 -Ecj C:\path\ecj-3.46.100.jar -Tedious C:\path\node_modules\tedious

# The two drivers that ship with Windows, with Encrypt=no: DaffyTee still requires encryption,
# and they encrypt the whole session.
powershell -File scripts\capture-client-init.ps1 -Only odbc_legacy,oledb_legacy -EncryptionOff -Port 14362
```

| Client | Version on this machine |
|---|---|
| Microsoft.Data.SqlClient | 6.1.1 on .NET 10 (SDK 10.0.401) |
| System.Data.SqlClient | .NET Framework 4.8.1 (`System.Data.dll` 4.8.10001.0), Windows PowerShell 5.1 |
| ODBC Driver 17 for SQL Server | 17.10.6.1 |
| ODBC Driver 18 for SQL Server | 18.6.1.1 |
| OLE DB Driver 19 for SQL Server (MSOLEDBSQL19) | 19.4.1.0 |
| SQL Server ODBC driver (`SQLSRV32`, ships with Windows) | 10.0.26100.9974 |
| SQLOLEDB provider (ships with Windows) | 10.0.26100.9974 |
| sqlcmd (classic, on ODBC Driver 17) | 15.0.1300.359 |
| go-sqlcmd (on go-mssqldb) | 1.10.0 |
| Microsoft JDBC Driver for SQL Server | 13.4.0 (`jre11` jar) on Java 25.0.3 |
| tedious | 20.3.9 on Node.js 25.2.1 |

Not covered: SSMS, DBeaver and Power BI. They are GUI tools and need a person at the keyboard.

## Where DaffyTee records a session's start

| Output | What it shows about a session's start |
|---|---|
| `--log-file` | `session.accepted` when a client connects. `session.login7` is the **LOGIN7 summary**: user, host, application, server name, database, TDS version, packet size, the feature extensions asked for, the client interface name, its program version, PID, LCID and time zone, the option flags (`flags=` OptionFlags1-OptionFlags2-TypeFlags-OptionFlags3), and the client-side encryption. It is a debug entry, so the file always has it, and the console shows it unless `--quiet` is set. Then `session.open` (SPID, handshake timings) and `session.close`. A client that hangs up before logging in gets `session.closed_early`, which says when it left. |
| `--sink file` | `session.start` (`clientSecurity`, `targetSecurity`, `serverVersion`, `mars`), one `request.*` per request, and `session.end`. A refused login is `session.login_failed`. |
| `--sql-log` | The same events, written as runnable T-SQL with a comment line for each. |
| `--transcript DIR` | Every decoded TDS message in both directions, starting **after** the login. PRELOGIN and LOGIN7 (which carries the password) are never in it. |

The PRELOGIN exchange is recorded only as its outcome (`clientSecurity`), and LOGIN7 only as the
summary above.

## Summary

| Client | `app` in LOGIN7 | TDS | Packet size | Server name sent | Feature extensions asked for | Sent after login |
|---|---|---|---|---|---|---|
| Microsoft.Data.SqlClient 6.1.1 | `Core Microsoft SqlClient Data Provider` | 7.4 | 8000 | `tcp:127.0.0.1,14361` | SESSIONRECOVERY, COLUMNENCRYPTION, GLOBALTRANSACTIONS, DATACLASSIFICATION, UTF8SUPPORT, SQLDNSCACHING, JSONSUPPORT, VECTORSUPPORT | the query only |
| System.Data.SqlClient (.NET Framework) | `.Net SqlClient Data Provider` | 7.4 | 8000 | `tcp:127.0.0.1,14361` | SESSIONRECOVERY, COLUMNENCRYPTION, GLOBALTRANSACTIONS | the query only |
| ODBC Driver 17 | `Microsoft® Windows® Operating System` | 7.4 | 0 | `tcp:127.0.0.1,14361` | SQLDNSCACHING, DATACLASSIFICATION, UTF8SUPPORT, SESSIONRECOVERY | the query only |
| ODBC Driver 18 | `Microsoft® Windows® Operating System` | 7.4 | 0 | `tcp:127.0.0.1,14361` | SESSIONRECOVERY, DATACLASSIFICATION, UTF8SUPPORT, SQLDNSCACHING | the query only |
| OLE DB Driver 19 | `Microsoft® Windows® Operating System` | 7.4 | 0 | `tcp:127.0.0.1,14361` | SESSIONRECOVERY, DATACLASSIFICATION, UTF8SUPPORT | the query only |
| sqlcmd 15 (ODBC 17) | `SQLCMD` | 7.4 | 4096 | `tcp:127.0.0.1,14361` | SQLDNSCACHING, DATACLASSIFICATION, UTF8SUPPORT, SESSIONRECOVERY | `SET QUOTED_IDENTIFIER OFF`, then `SET TEXTSIZE 4096`, then the query: three batches |
| go-sqlcmd 1.10.0 | `sqlcmd` | 7.4 | 4096 | `127.0.0.1` | none | the query only, flagged as a connection reset |
| JDBC 13.4.0 | `Microsoft JDBC Driver for SQL Server` | 7.4 | 8000 | `127.0.0.1` | USERAGENT, COLUMNENCRYPTION, DATACLASSIFICATION, UTF8SUPPORT, SQLDNSCACHING, VECTORSUPPORT, JSONSUPPORT, SESSIONRECOVERY | the query only |
| tedious 20.3.9 | `Tedious` | 7.4 | 4096 | `127.0.0.1` | UTF8SUPPORT | a batch of 15 `SET` statements, then the query as `sp_executesql` |
| SQLSRV32 ODBC (Windows), `Encrypt=no` | `Microsoft® Windows® Operating System` | 7.1 | 0 | `tcp:127.0.0.1,14362` | none (TDS 7.1 has none) | the query only, without `ALL_HEADERS` |
| SQLOLEDB (Windows), `Encrypt=no` | `Microsoft® Windows® Operating System` | 7.1 | 4096 | `tcp:127.0.0.1,14362` | none | the query only, without `ALL_HEADERS` |

With `Encrypt=yes`, the two Windows drivers never log in: they check DaffyTee's certificate,
cannot be told to trust a self-signed one, and hang up after TLS.

The rest of each LOGIN7, from the same summaries:

| Client | `interface` | `progVer` | LCID, time zone | `flags` (OptionFlags1-2, TypeFlags, OptionFlags3) |
|---|---|---|---|---|
| Microsoft.Data.SqlClient 6.1.1 | `Core Microsoft SqlClient Data Provider` | `0x06000000` | 0, 0 | `E0-03-00-10` |
| System.Data.SqlClient | `.Net SqlClient Data Provider` | `0x06000000` | 0, 0 | `E0-03-00-10` |
| ODBC Driver 17 and 18, sqlcmd 15 | `ODBC` | `0x07000000` | `0x0409`, local | `E0-03-00-10` |
| OLE DB Driver 19 | `OLEDB` | `0x07000000` | `0x0409`, local | `E0-01-10-10` |
| go-sqlcmd 1.10.0 | `go-mssqldb` | `0x01090008` | 0, 0 | `A0-02-00-00` |
| JDBC 13.4.0 | `Microsoft JDBC Driver 13.4` | `0x0D040000` | 0, 0 | `E0-03-00-18` |
| tedious 20.3.9 | `Tedious` | `0x00000000` | `0x0409`, local | `F0-00-00-18` |
| SQLSRV32 ODBC (Windows) | `ODBC` | `0x07000000` | `0x0409`, local | `E0-03-00-08` |
| SQLOLEDB (Windows) | `OLEDB` | `0x07000000` | `0x0409`, local | `E0-03-10-08` |

## The same for every client

- **Encryption.** Every session was encrypted with TLS 1.2 inside PRELOGIN packets.
  `clientSecurity` is `Mandatory (TDS 7.4), Tls12` for the clients that asked for encryption. The
  two Windows drivers, with `Encrypt=no`, show `Optional->upgraded (TDS 7.1), Tls12`: DaffyTee
  answered that encryption is required, and they encrypted the whole session. None used TDS 8.0
  (`Encrypt=Strict`), because none was asked to.
- **No database named.** Every LOGIN7 had `db=''`, so each session opened in the mapping's
  default database, `DaffyTeeTest`.
- **One header, from TDS 7.2.** Every TDS 7.4 request carried `ALL_HEADERS` with a single
  `TRANSACTION_DESCRIPTOR`: no transaction (`0x0000000000000000`) and one outstanding request.
  TDS 7.1 requests have no `ALL_HEADERS`; the transcript says `ALL_HEADERS: absent`.
- **The query.** `SELECT @@VERSION` went as a `SQL_BATCH` from every client but tedious, which
  sent an RPC: 54 bytes with `ALL_HEADERS`, 32 bytes from the TDS 7.1 drivers. Every reply was one
  packet: `COLMETADATA` (one `nvarchar` column), one `ROW`, then `DONE`.
- **The end.** Each session ended by the client closing its connection, because TDS has no logout
  message. go-sqlcmd reset the TCP connection rather than closing it, and `session.end` says so:
  `client disconnected (An existing connection was forcibly closed by the remote host.)`.

## What differs

- **Application name.** SqlClient (both), JDBC, tedious and both sqlcmds name themselves. The
  ODBC and OLE DB drivers had no `APP=` / `Application Name=` to go by, so they sent the host
  program's product name: `Microsoft® Windows® Operating System`, from `powershell.exe`. In
  DaffyTee's records, every ODBC or OLE DB application started from the same kind of host looks
  alike. Setting an application name in the connection string is what tells them apart in
  `session.app`, the RabbitMQ `daffytee-app` header and `:sessions`. The `interface` field still
  says which library it was.
- **Packet size.** 8000 for both SqlClients and JDBC. 4096 for both sqlcmds, tedious and
  SQLOLEDB. 0 for the ODBC drivers and OLE DB 19, which means "whatever the server uses".
- **Server name.** The Microsoft native and .NET drivers send the server exactly as written
  (`tcp:127.0.0.1,14361`). go-sqlcmd, JDBC and tedious send only the host.
- **Feature extensions.** These range from none (go-sqlcmd, and the TDS 7.1 drivers, whose
  version has no FeatureExt) to eight (Microsoft.Data.SqlClient 6.1.1 and JDBC 13.4). The clients
  that ask for COLUMNENCRYPTION (both SqlClients and JDBC) get `COLMETADATA` with a CEK table
  (`cekTable=0` in the transcript). The others get it without one.
- **Option flags.** OptionFlags2 holds `ODBC_ON` (0x02), which MS-TDS says makes the server
  turn `ANSI_DEFAULTS` on for the session. The SqlClients, the ODBC drivers, JDBC, sqlcmd and the
  Windows drivers send it (`03`), and go-mssqldb does too (`02`). OLE DB Driver 19 (`01`) and
  tedious (`00`) do not. tedious makes up for it with its batch of `SET` statements. TypeFlags
  `10` marks both OLE DB providers (`fOLEDB`). OptionFlags3 `10` is the TDS 7.4 FeatureExt bit;
  JDBC and tedious add 0x08 (unknown-collation handling), and the Windows drivers send 0x08 in
  what is still a reserved byte in TDS 7.1.
- **Session settings.** Only classic sqlcmd and tedious set anything with SQL:
  - sqlcmd sends `SET QUOTED_IDENTIFIER OFF` (its default without `-I`) and `SET TEXTSIZE 4096`,
    each as its own batch. Because the session asked for session recovery, the server answers
    each with a `SESSIONSTATE` token.
  - tedious sends one batch of fifteen `SET` statements. Among them is `set language us_english`,
    which the server acknowledges with an `ENVCHANGE` and INFO 5703.
- **A reset on a fresh connection.** go-sqlcmd marks its first and only request with the
  reset-connection flag. DaffyTee records `resetConnection: true`, and the SQL log says
  `-- (pooled connection reset before this request)`, although nothing was pooled. The server
  acknowledges the reset with `ENVCHANGE` type 18.
- **Even a plain query is an RPC for tedious.** `execSql` sends `sp_executesql`, so DaffyTee
  files `SELECT @@VERSION` as `request.rpc`, not `request.batch`.
- **TDS 7.1 is a different shape throughout.** Besides the LOGIN7 and the missing `ALL_HEADERS`:
  `DONE` carries a 4-byte row count, `COLMETADATA` a 2-byte UserType, and `ERROR`/`INFO` a 2-byte
  line number. SQL Server answers a TDS 7.1 client in that shape, and DaffyTee now reads it so.
  `daffytee.*` commands are not answered for clients older than TDS 7.3; they reach SQL Server,
  which says it has no such procedure.

## Findings, and what was fixed

The first run of this capture, against DaffyTee 0.3.1, turned up four problems. All are fixed
in 0.3.2, and checked by unit tests (fixtures captured from the drivers
and from SQL Server itself) and by `scripts/verify-relay.ps1`.

1. **A TDS 7.1 login was refused as a password change.** The two drivers that ship with Windows
   send a TDS 7.1 LOGIN7, whose fixed part is 86 bytes. `Login7.Parse` read the change-password
   offset and length at byte 86 whatever the version: a field TDS 7.2 added. In a 7.1 login those
   bytes are the start of the first string, the host name, so they read as a change-password
   entry that was not empty. DaffyTee refused the login (`reason: password_change`), and its
   refusal, written in the TDS 7.2 shape, made the drivers report "SQL Server version 6.5 and all
   previous versions are no longer supported" (ODBC) or "DBMS version is less than 7.0.0" (OLE
   DB). **Fixed:**
   - LOGIN7 is read and written with the fixed part its version has, with no change-password
     entry, cbSSPILong or FeatureExt before TDS 7.2.
   - DaffyTee's own refusal is written with a 2-byte line number and a 4-byte DONE row count for
     those clients, matching SQL Server's own refusal byte for byte in its DONE.
   - Responses are read by the session's TDS version in the event decoder, the transcript and
     the relay's final-DONE tracking (row count, attention), and in the login response.
   - `clientSecurity` names the client's own TDS version.
   - An Entra ID mapping refuses a client older than TDS 7.4 as `bad_mapping`, with a reason.
     FEDAUTH needs FeatureExt.

   With `Encrypt=no`, both drivers now log in, run queries and are captured in full. With
   `Encrypt=yes` they still need a certificate they trust: `daffytee cert trust` should do it, but
   that was not tried, because it needs a person to confirm.
2. **A client that gave up during the handshake left no reason behind.** With `Encrypt=yes`, the
   Windows drivers reject DaffyTee's self-signed certificate after the TLS handshake and retry
   about 20 times. DaffyTee logged only `session.accepted` for each attempt, and the same for any
   connection that closed before PRELOGIN. **Fixed:** both now log `session.closed_early` with
   the reason. After TLS, the entry says that this is what a client does when it does not trust
   DaffyTee's certificate, and points to `daffytee cert`.
3. **Feature extension 0x10 was unnamed.** Microsoft JDBC 13.4.0 asks for feature 0x10, which the
   driver's own `TDS` class names `TDS_FEATURE_EXT_USERAGENT`. **Fixed:** it is logged as
   `USERAGENT`.
4. **The LOGIN7 summary left out the option flags**, the client interface name, program version,
   PID, LCID and time zone. **Fixed:** they are in `session.login7` now, as in the second table
   above.

## Each client, as captured

Each section shows, in order: what the client printed, DaffyTee's operational log for its
session(s), the SQL log, the transcript, and the events (folded).

### Microsoft.Data.SqlClient 6.1.1

**Microsoft.Data.SqlClient 6.1.1 (.NET 10)**

Connection string: `Server=tcp:127.0.0.1,14361;User Id=mds611;Password=...;Encrypt=Mandatory;TrustServerCertificate=True`. The test is a .NET 10 file-based app (`dotnet run mds.cs`). It asks for the most feature extensions of any client here, alongside JDBC, and sends nothing but the query. Column encryption is negotiated, so the reply's `COLMETADATA` carries a CEK table (`cekTable=0`).

The client printed (exit code 0):

```text
Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64)
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:28:33.0683235Z","level":"debug","event":"session.accepted","session":2,"msg":"connection from 127.0.0.1:62201"}
{"ts":"2026-11-16T04:28:33.113336Z","level":"debug","event":"session.login7","session":2,"msg":"LOGIN7 user='mds611' host='DEV01' app='Core Microsoft SqlClient Data Provider' server='tcp:127.0.0.1,14361' db='' tds=7.4 packetSize=8000 features=[SESSIONRECOVERY,COLUMNENCRYPTION,GLOBALTRANSACTIONS,DATACLASSIFICATION,UTF8SUPPORT,SQLDNSCACHING,JSONSUPPORT,VECTORSUPPORT] interface='Core Microsoft SqlClient Data Provider' progVer=0x06000000 pid=48496 lcid=0x0000 tz=0 flags=E0-03-00-10 via Mandatory (TDS 7.4), Tls12"}
{"ts":"2026-11-16T04:28:33.1597868Z","level":"info","event":"session.open","session":2,"msg":"mds611 -> svc_app@127.0.0.1:14333 (spid 58, Microsoft SQL Server 16.0.4295) app='Core Microsoft SqlClient Data Provider' host='DEV01' db='DaffyTeeTest' from 127.0.0.1:62201 [handshake 88 ms: client tls 37, auth 19, target login 16]"}
{"ts":"2026-11-16T04:28:33.1989739Z","level":"info","event":"session.close","session":2,"msg":"closed: client disconnected; 1 request(s), 62 B in, 440 B out, 00:00:00"}
```

**SQL log** (`--sql-log`, local time):

```sql
-- 2026-11-15 22:28:33.161  s2  mds611 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Core Microsoft SqlClient Data Provider  session.start  clientSecurity=Mandatory (TDS 7.4), Tls12 targetSecurity=Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 serverVersion=Microsoft SQL Server 16.0.4295 mars=False
-- 2026-11-15 22:28:33.177  s2#1  mds611 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Core Microsoft SqlClient Data Provider  5.49 ms  ok  1 row(s)
SELECT @@VERSION
GO
-- 2026-11-15 22:28:33.199  s2  mds611 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Core Microsoft SqlClient Data Provider  session.end  requests=1 bytesFromClient=62 bytesFromTarget=440 reason=client disconnected
```

**Transcript** (`--transcript`; it starts after the login, so PRELOGIN and LOGIN7 are not in it):

```text
### session 2 login='mds611' -> 127.0.0.1:14333 app='Core Microsoft SqlClient Data Provider' host='DEV01' from=127.0.0.1:62201
### target svc_app@127.0.0.1:14333 spid=58 version=Microsoft SQL Server 16.0.4295
### client side: Mandatory (TDS 7.4), Tls12; target side: Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256

=== CLIENT --> TARGET  SQL_BATCH  +10.2ms
  type=0x01 SQL_BATCH  packets=1  payload=54 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    SQL: SELECT @@VERSION

=== TARGET --> CLIENT  TABULAR_RESULT  +15.7ms
  type=0x04 TABULAR_RESULT  packets=1  payload=432 bytes
    [0000] 0x81 COLMETADATA   count=1 cekTable=0
                       col[0]               NVARCHAR(600 bytes)  flags=0x0021 userType=0
    [0014] 0xD1 ROW           ="Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) 
	Aug 26 2026 11:02:22 
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>"
    [01A3] 0xFD DONE          status=0x0010 [COUNT] curcmd=0x00C1 rows=1

### closed: client disconnected
```

<details><summary><b>Events</b> (<code>--sink file</code>): 3 - session.start, request.batch, session.end</summary>

```json
{
  "specversion": "1.0",
  "id": "01a1e29d-2a09-7403-8f61-20ed2bff04ae",
  "source": "daffytee://DEV01:14361",
  "type": "session.start",
  "time": "2026-11-16T04:28:33.1610056Z",
  "datacontenttype": "application/json",
  "partitionkey": "s2",
  "data": {
    "v": 1,
    "session": {
      "id": 2,
      "connectionId": "01a1e29d-29ac-79ea-9527-b24c902fe7ff",
      "login": "mds611",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Core Microsoft SqlClient Data Provider",
      "client": "127.0.0.1:62201"
    },
    "clientSecurity": "Mandatory (TDS 7.4), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-2a2a-7d1c-8535-1896edd898c8",
  "source": "daffytee://DEV01:14361",
  "type": "request.batch",
  "time": "2026-11-16T04:28:33.1772862Z",
  "datacontenttype": "application/json",
  "partitionkey": "s2",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 5.488,
    "session": {
      "id": 2,
      "connectionId": "01a1e29d-29ac-79ea-9527-b24c902fe7ff",
      "login": "mds611",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Core Microsoft SqlClient Data Provider",
      "client": "127.0.0.1:62201"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "bytes": 54,
      "packets": 1,
      "sql": "SELECT @@VERSION"
    },
    "response": {
      "status": "ok",
      "rowCount": 1,
      "done": "DONE",
      "doneStatus": "0x0010",
      "bytes": 432,
      "packets": 1,
      "firstByteMs": 5.488,
      "rowCounts": [
        1
      ],
      "resultSets": [
        1
      ]
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-2a2f-7288-ae77-68f93f6b382f",
  "source": "daffytee://DEV01:14361",
  "type": "session.end",
  "time": "2026-11-16T04:28:33.1993626Z",
  "datacontenttype": "application/json",
  "partitionkey": "s2",
  "data": {
    "v": 1,
    "durationMs": 131.1,
    "session": {
      "id": 2,
      "connectionId": "01a1e29d-29ac-79ea-9527-b24c902fe7ff",
      "login": "mds611",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Core Microsoft SqlClient Data Provider",
      "client": "127.0.0.1:62201"
    },
    "requests": 1,
    "bytesFromClient": 62,
    "bytesFromTarget": 440,
    "reason": "client disconnected"
  }
}
```

</details>

### System.Data.SqlClient (.NET Framework)

**System.Data.SqlClient (.NET Framework 4.8, Windows PowerShell 5.1)**

The SqlClient built into .NET Framework, run from Windows PowerShell 5.1 (`New-Object System.Data.SqlClient.SqlConnection`). Connection string: `Server=tcp:127.0.0.1,14361;User Id=sds_netfx;Password=...;Encrypt=True;TrustServerCertificate=True`. It behaves like Microsoft.Data.SqlClient with fewer feature extensions, and names itself `.Net SqlClient Data Provider`.

The client printed (exit code 0):

```text
Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64)
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:28:34.9039593Z","level":"debug","event":"session.accepted","session":3,"msg":"connection from 127.0.0.1:62203"}
{"ts":"2026-11-16T04:28:34.9105162Z","level":"debug","event":"session.login7","session":3,"msg":"LOGIN7 user='sds_netfx' host='DEV01' app='.Net SqlClient Data Provider' server='tcp:127.0.0.1,14361' db='' tds=7.4 packetSize=8000 features=[SESSIONRECOVERY,COLUMNENCRYPTION,GLOBALTRANSACTIONS] interface='.Net SqlClient Data Provider' progVer=0x06000000 pid=24852 lcid=0x0000 tz=0 flags=E0-03-00-10 via Mandatory (TDS 7.4), Tls12"}
{"ts":"2026-11-16T04:28:34.9374628Z","level":"info","event":"session.open","session":3,"msg":"sds_netfx -> svc_app@127.0.0.1:14333 (spid 58, Microsoft SQL Server 16.0.4295) app='.Net SqlClient Data Provider' host='DEV01' db='DaffyTeeTest' from 127.0.0.1:62203 [handshake 30 ms: client tls 2, auth 17, target login 8]"}
{"ts":"2026-11-16T04:28:34.9546106Z","level":"info","event":"session.close","session":3,"msg":"closed: client disconnected; 1 request(s), 62 B in, 440 B out, 00:00:00"}
```

**SQL log** (`--sql-log`, local time):

```sql
-- 2026-11-15 22:28:34.938  s3  sds_netfx -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=.Net SqlClient Data Provider  session.start  clientSecurity=Mandatory (TDS 7.4), Tls12 targetSecurity=Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 serverVersion=Microsoft SQL Server 16.0.4295 mars=False
-- 2026-11-15 22:28:34.941  s3#1  sds_netfx -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=.Net SqlClient Data Provider  1.44 ms  ok  1 row(s)
SELECT @@VERSION
GO
-- 2026-11-15 22:28:34.955  s3  sds_netfx -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=.Net SqlClient Data Provider  session.end  requests=1 bytesFromClient=62 bytesFromTarget=440 reason=client disconnected
```

**Transcript** (`--transcript`; it starts after the login, so PRELOGIN and LOGIN7 are not in it):

```text
### session 3 login='sds_netfx' -> 127.0.0.1:14333 app='.Net SqlClient Data Provider' host='DEV01' from=127.0.0.1:62203
### target svc_app@127.0.0.1:14333 spid=58 version=Microsoft SQL Server 16.0.4295
### client side: Mandatory (TDS 7.4), Tls12; target side: Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256

=== CLIENT --> TARGET  SQL_BATCH  +2.4ms
  type=0x01 SQL_BATCH  packets=1  payload=54 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    SQL: SELECT @@VERSION

=== TARGET --> CLIENT  TABULAR_RESULT  +3.8ms
  type=0x04 TABULAR_RESULT  packets=1  payload=432 bytes
    [0000] 0x81 COLMETADATA   count=1 cekTable=0
                       col[0]               NVARCHAR(600 bytes)  flags=0x0021 userType=0
    [0014] 0xD1 ROW           ="Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) 
	Aug 26 2026 11:02:22 
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>"
    [01A3] 0xFD DONE          status=0x0010 [COUNT] curcmd=0x00C1 rows=1

### closed: client disconnected
```

<details><summary><b>Events</b> (<code>--sink file</code>): 3 - session.start, request.batch, session.end</summary>

```json
{
  "specversion": "1.0",
  "id": "01a1e29d-30fa-7d83-874f-51ac4552a809",
  "source": "daffytee://DEV01:14361",
  "type": "session.start",
  "time": "2026-11-16T04:28:34.9383838Z",
  "datacontenttype": "application/json",
  "partitionkey": "s3",
  "data": {
    "v": 1,
    "session": {
      "id": 3,
      "connectionId": "01a1e29d-30d7-73c6-8d18-32b2013701c0",
      "login": "sds_netfx",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": ".Net SqlClient Data Provider",
      "client": "127.0.0.1:62203"
    },
    "clientSecurity": "Mandatory (TDS 7.4), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-30ff-7374-aced-82a5d6464f1e",
  "source": "daffytee://DEV01:14361",
  "type": "request.batch",
  "time": "2026-11-16T04:28:34.9415974Z",
  "datacontenttype": "application/json",
  "partitionkey": "s3",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 1.437,
    "session": {
      "id": 3,
      "connectionId": "01a1e29d-30d7-73c6-8d18-32b2013701c0",
      "login": "sds_netfx",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": ".Net SqlClient Data Provider",
      "client": "127.0.0.1:62203"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "bytes": 54,
      "packets": 1,
      "sql": "SELECT @@VERSION"
    },
    "response": {
      "status": "ok",
      "rowCount": 1,
      "done": "DONE",
      "doneStatus": "0x0010",
      "bytes": 432,
      "packets": 1,
      "firstByteMs": 1.437,
      "rowCounts": [
        1
      ],
      "resultSets": [
        1
      ]
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-310b-757a-b2b1-c26949765527",
  "source": "daffytee://DEV01:14361",
  "type": "session.end",
  "time": "2026-11-16T04:28:34.9554107Z",
  "datacontenttype": "application/json",
  "partitionkey": "s3",
  "data": {
    "v": 1,
    "durationMs": 51.5,
    "session": {
      "id": 3,
      "connectionId": "01a1e29d-30d7-73c6-8d18-32b2013701c0",
      "login": "sds_netfx",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": ".Net SqlClient Data Provider",
      "client": "127.0.0.1:62203"
    },
    "requests": 1,
    "bytesFromClient": 62,
    "bytesFromTarget": 440,
    "reason": "client disconnected"
  }
}
```

</details>

### ODBC Driver 17 for SQL Server

**ODBC Driver 17 for SQL Server (System.Data.Odbc)**

Through `System.Data.Odbc` in Windows PowerShell 5.1. Connection string: `Driver={ODBC Driver 17 for SQL Server};Server=tcp:127.0.0.1,14361;Uid=odbc17;Pwd=...;Encrypt=yes;TrustServerCertificate=yes`. No `APP=` was given, so the application name is the host program's product name. Packet size 0 lets the server choose.

The client printed (exit code 0):

```text
Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64)
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:28:36.6493918Z","level":"debug","event":"session.accepted","session":4,"msg":"connection from 127.0.0.1:62205"}
{"ts":"2026-11-16T04:28:36.6549929Z","level":"debug","event":"session.login7","session":4,"msg":"LOGIN7 user='odbc17' host='DEV01' app='Microsoft® Windows® Operating System' server='tcp:127.0.0.1,14361' db='' tds=7.4 packetSize=0 features=[SQLDNSCACHING,DATACLASSIFICATION,UTF8SUPPORT,SESSIONRECOVERY] interface='ODBC' progVer=0x07000000 pid=10900 lcid=0x0409 tz=360 flags=E0-03-00-10 via Mandatory (TDS 7.4), Tls12"}
{"ts":"2026-11-16T04:28:36.6792277Z","level":"info","event":"session.open","session":4,"msg":"odbc17 -> svc_app@127.0.0.1:14333 (spid 58, Microsoft SQL Server 16.0.4295) app='Microsoft® Windows® Operating System' host='DEV01' db='DaffyTeeTest' from 127.0.0.1:62205 [handshake 27 ms: client tls 2, auth 16, target login 7]"}
{"ts":"2026-11-16T04:28:36.6997985Z","level":"info","event":"session.close","session":4,"msg":"closed: client disconnected; 1 request(s), 62 B in, 438 B out, 00:00:00"}
```

**SQL log** (`--sql-log`, local time):

```sql
-- 2026-11-15 22:28:36.679  s4  odbc17 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  session.start  clientSecurity=Mandatory (TDS 7.4), Tls12 targetSecurity=Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 serverVersion=Microsoft SQL Server 16.0.4295 mars=False
-- 2026-11-15 22:28:36.687  s4#1  odbc17 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  0.98 ms  ok  1 row(s)
SELECT @@VERSION
GO
-- 2026-11-15 22:28:36.700  s4  odbc17 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  session.end  requests=1 bytesFromClient=62 bytesFromTarget=438 reason=client disconnected
```

**Transcript** (`--transcript`; it starts after the login, so PRELOGIN and LOGIN7 are not in it):

```text
### session 4 login='odbc17' -> 127.0.0.1:14333 app='Microsoft® Windows® Operating System' host='DEV01' from=127.0.0.1:62205
### target svc_app@127.0.0.1:14333 spid=58 version=Microsoft SQL Server 16.0.4295
### client side: Mandatory (TDS 7.4), Tls12; target side: Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256

=== CLIENT --> TARGET  SQL_BATCH  +7.2ms
  type=0x01 SQL_BATCH  packets=1  payload=54 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    SQL: SELECT @@VERSION

=== TARGET --> CLIENT  TABULAR_RESULT  +8.2ms
  type=0x04 TABULAR_RESULT  packets=1  payload=430 bytes
    [0000] 0x81 COLMETADATA   count=1
                       col[0]               NVARCHAR(600 bytes)  flags=0x0021 userType=0
    [0012] 0xD1 ROW           ="Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) 
	Aug 26 2026 11:02:22 
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>"
    [01A1] 0xFD DONE          status=0x0010 [COUNT] curcmd=0x00C1 rows=1

### closed: client disconnected
```

<details><summary><b>Events</b> (<code>--sink file</code>): 3 - session.start, request.batch, session.end</summary>

```json
{
  "specversion": "1.0",
  "id": "01a1e29d-37c7-7de7-a6ef-386a637611fd",
  "source": "daffytee://DEV01:14361",
  "type": "session.start",
  "time": "2026-11-16T04:28:36.6799579Z",
  "datacontenttype": "application/json",
  "partitionkey": "s4",
  "data": {
    "v": 1,
    "session": {
      "id": 4,
      "connectionId": "01a1e29d-37a9-74cb-aeee-35a61d4f9613",
      "login": "odbc17",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:62205"
    },
    "clientSecurity": "Mandatory (TDS 7.4), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-37d1-7adc-a698-c5473606910a",
  "source": "daffytee://DEV01:14361",
  "type": "request.batch",
  "time": "2026-11-16T04:28:36.6879311Z",
  "datacontenttype": "application/json",
  "partitionkey": "s4",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 0.978,
    "session": {
      "id": 4,
      "connectionId": "01a1e29d-37a9-74cb-aeee-35a61d4f9613",
      "login": "odbc17",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:62205"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "bytes": 54,
      "packets": 1,
      "sql": "SELECT @@VERSION"
    },
    "response": {
      "status": "ok",
      "rowCount": 1,
      "done": "DONE",
      "doneStatus": "0x0010",
      "bytes": 430,
      "packets": 1,
      "firstByteMs": 0.978,
      "rowCounts": [
        1
      ],
      "resultSets": [
        1
      ]
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-37dc-77ec-9d7b-09fc21b74a40",
  "source": "daffytee://DEV01:14361",
  "type": "session.end",
  "time": "2026-11-16T04:28:36.7005574Z",
  "datacontenttype": "application/json",
  "partitionkey": "s4",
  "data": {
    "v": 1,
    "durationMs": 51.2,
    "session": {
      "id": 4,
      "connectionId": "01a1e29d-37a9-74cb-aeee-35a61d4f9613",
      "login": "odbc17",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:62205"
    },
    "requests": 1,
    "bytesFromClient": 62,
    "bytesFromTarget": 438,
    "reason": "client disconnected"
  }
}
```

</details>

### ODBC Driver 18 for SQL Server

**ODBC Driver 18 for SQL Server (System.Data.Odbc)**

Same as ODBC Driver 17 apart from the driver name. Connection string: `Driver={ODBC Driver 18 for SQL Server};Server=tcp:127.0.0.1,14361;Uid=odbc18;Pwd=...;Encrypt=yes;TrustServerCertificate=yes`. It asks for the same four feature extensions in a different order. On the wire it is indistinguishable from 17 here.

The client printed (exit code 0):

```text
Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64)
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:28:38.3756131Z","level":"debug","event":"session.accepted","session":5,"msg":"connection from 127.0.0.1:62207"}
{"ts":"2026-11-16T04:28:38.3800586Z","level":"debug","event":"session.login7","session":5,"msg":"LOGIN7 user='odbc18' host='DEV01' app='Microsoft® Windows® Operating System' server='tcp:127.0.0.1,14361' db='' tds=7.4 packetSize=0 features=[SESSIONRECOVERY,DATACLASSIFICATION,UTF8SUPPORT,SQLDNSCACHING] interface='ODBC' progVer=0x07000000 pid=28336 lcid=0x0409 tz=360 flags=E0-03-00-10 via Mandatory (TDS 7.4), Tls12"}
{"ts":"2026-11-16T04:28:38.4054457Z","level":"info","event":"session.open","session":5,"msg":"odbc18 -> svc_app@127.0.0.1:14333 (spid 58, Microsoft SQL Server 16.0.4295) app='Microsoft® Windows® Operating System' host='DEV01' db='DaffyTeeTest' from 127.0.0.1:62207 [handshake 29 ms: client tls 3, auth 17, target login 7]"}
{"ts":"2026-11-16T04:28:38.4267493Z","level":"info","event":"session.close","session":5,"msg":"closed: client disconnected; 1 request(s), 62 B in, 438 B out, 00:00:00"}
```

**SQL log** (`--sql-log`, local time):

```sql
-- 2026-11-15 22:28:38.406  s5  odbc18 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  session.start  clientSecurity=Mandatory (TDS 7.4), Tls12 targetSecurity=Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 serverVersion=Microsoft SQL Server 16.0.4295 mars=False
-- 2026-11-15 22:28:38.414  s5#1  odbc18 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  1.16 ms  ok  1 row(s)
SELECT @@VERSION
GO
-- 2026-11-15 22:28:38.427  s5  odbc18 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  session.end  requests=1 bytesFromClient=62 bytesFromTarget=438 reason=client disconnected
```

**Transcript** (`--transcript`; it starts after the login, so PRELOGIN and LOGIN7 are not in it):

```text
### session 5 login='odbc18' -> 127.0.0.1:14333 app='Microsoft® Windows® Operating System' host='DEV01' from=127.0.0.1:62207
### target svc_app@127.0.0.1:14333 spid=58 version=Microsoft SQL Server 16.0.4295
### client side: Mandatory (TDS 7.4), Tls12; target side: Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256

=== CLIENT --> TARGET  SQL_BATCH  +7.4ms
  type=0x01 SQL_BATCH  packets=1  payload=54 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    SQL: SELECT @@VERSION

=== TARGET --> CLIENT  TABULAR_RESULT  +8.5ms
  type=0x04 TABULAR_RESULT  packets=1  payload=430 bytes
    [0000] 0x81 COLMETADATA   count=1
                       col[0]               NVARCHAR(600 bytes)  flags=0x0021 userType=0
    [0012] 0xD1 ROW           ="Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) 
	Aug 26 2026 11:02:22 
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>"
    [01A1] 0xFD DONE          status=0x0010 [COUNT] curcmd=0x00C1 rows=1

### closed: client disconnected
```

<details><summary><b>Events</b> (<code>--sink file</code>): 3 - session.start, request.batch, session.end</summary>

```json
{
  "specversion": "1.0",
  "id": "01a1e29d-3e86-77e2-bc4b-e99f09d7a4cf",
  "source": "daffytee://DEV01:14361",
  "type": "session.start",
  "time": "2026-11-16T04:28:38.4062484Z",
  "datacontenttype": "application/json",
  "partitionkey": "s5",
  "data": {
    "v": 1,
    "session": {
      "id": 5,
      "connectionId": "01a1e29d-3e67-7a97-a43f-d16c86f09f6e",
      "login": "odbc18",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:62207"
    },
    "clientSecurity": "Mandatory (TDS 7.4), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-3e90-7d01-9e5d-84f8c73105fa",
  "source": "daffytee://DEV01:14361",
  "type": "request.batch",
  "time": "2026-11-16T04:28:38.4142321Z",
  "datacontenttype": "application/json",
  "partitionkey": "s5",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 1.162,
    "session": {
      "id": 5,
      "connectionId": "01a1e29d-3e67-7a97-a43f-d16c86f09f6e",
      "login": "odbc18",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:62207"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "bytes": 54,
      "packets": 1,
      "sql": "SELECT @@VERSION"
    },
    "response": {
      "status": "ok",
      "rowCount": 1,
      "done": "DONE",
      "doneStatus": "0x0010",
      "bytes": 430,
      "packets": 1,
      "firstByteMs": 1.162,
      "rowCounts": [
        1
      ],
      "resultSets": [
        1
      ]
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-3e9b-7636-9876-f02f32f17a21",
  "source": "daffytee://DEV01:14361",
  "type": "session.end",
  "time": "2026-11-16T04:28:38.4275164Z",
  "datacontenttype": "application/json",
  "partitionkey": "s5",
  "data": {
    "v": 1,
    "durationMs": 51.9,
    "session": {
      "id": 5,
      "connectionId": "01a1e29d-3e67-7a97-a43f-d16c86f09f6e",
      "login": "odbc18",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:62207"
    },
    "requests": 1,
    "bytesFromClient": 62,
    "bytesFromTarget": 438,
    "reason": "client disconnected"
  }
}
```

</details>

### OLE DB Driver 19 for SQL Server (MSOLEDBSQL19)

**OLE DB Driver 19 for SQL Server (MSOLEDBSQL19, System.Data.OleDb)**

Through `System.Data.OleDb` in Windows PowerShell 5.1. Connection string: `Provider=MSOLEDBSQL19;Data Source=tcp:127.0.0.1,14361;User ID=oledb19;Password=...;Use Encryption for Data=Mandatory;Trust Server Certificate=True`. Like the ODBC drivers, minus SQLDNSCACHING.

The client printed (exit code 0):

```text
Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64)
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:28:57.6338152Z","level":"debug","event":"session.accepted","session":25,"msg":"connection from 127.0.0.1:62229"}
{"ts":"2026-11-16T04:28:57.6405871Z","level":"debug","event":"session.login7","session":25,"msg":"LOGIN7 user='oledb19' host='DEV01' app='Microsoft® Windows® Operating System' server='tcp:127.0.0.1,14361' db='' tds=7.4 packetSize=0 features=[SESSIONRECOVERY,DATACLASSIFICATION,UTF8SUPPORT] interface='OLEDB' progVer=0x07000000 pid=35992 lcid=0x0409 tz=360 flags=E0-01-10-10 via Mandatory (TDS 7.4), Tls12"}
{"ts":"2026-11-16T04:28:57.666004Z","level":"info","event":"session.open","session":25,"msg":"oledb19 -> svc_app@127.0.0.1:14333 (spid 58, Microsoft SQL Server 16.0.4295) app='Microsoft® Windows® Operating System' host='DEV01' db='DaffyTeeTest' from 127.0.0.1:62229 [handshake 29 ms: client tls 2, auth 17, target login 7]"}
{"ts":"2026-11-16T04:28:57.6874481Z","level":"info","event":"session.close","session":25,"msg":"closed: client disconnected; 1 request(s), 62 B in, 438 B out, 00:00:00"}
```

**SQL log** (`--sql-log`, local time):

```sql
-- 2026-11-15 22:28:57.666  s25  oledb19 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  session.start  clientSecurity=Mandatory (TDS 7.4), Tls12 targetSecurity=Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 serverVersion=Microsoft SQL Server 16.0.4295 mars=False
-- 2026-11-15 22:28:57.675  s25#1  oledb19 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  1.07 ms  ok  1 row(s)
SELECT @@VERSION
GO
-- 2026-11-15 22:28:57.688  s25  oledb19 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  session.end  requests=1 bytesFromClient=62 bytesFromTarget=438 reason=client disconnected
```

**Transcript** (`--transcript`; it starts after the login, so PRELOGIN and LOGIN7 are not in it):

```text
### session 25 login='oledb19' -> 127.0.0.1:14333 app='Microsoft® Windows® Operating System' host='DEV01' from=127.0.0.1:62229
### target svc_app@127.0.0.1:14333 spid=58 version=Microsoft SQL Server 16.0.4295
### client side: Mandatory (TDS 7.4), Tls12; target side: Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256

=== CLIENT --> TARGET  SQL_BATCH  +7.1ms
  type=0x01 SQL_BATCH  packets=1  payload=54 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    SQL: SELECT @@VERSION

=== TARGET --> CLIENT  TABULAR_RESULT  +8.2ms
  type=0x04 TABULAR_RESULT  packets=1  payload=430 bytes
    [0000] 0x81 COLMETADATA   count=1
                       col[0]               NVARCHAR(600 bytes)  flags=0x0021 userType=0
    [0012] 0xD1 ROW           ="Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) 
	Aug 26 2026 11:02:22 
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>"
    [01A1] 0xFD DONE          status=0x0010 [COUNT] curcmd=0x00C1 rows=1

### closed: client disconnected
```

<details><summary><b>Events</b> (<code>--sink file</code>): 3 - session.start, request.batch, session.end</summary>

```json
{
  "specversion": "1.0",
  "id": "01a1e29d-89c2-7eb7-aeba-baf1a1f3cf63",
  "source": "daffytee://DEV01:14361",
  "type": "session.start",
  "time": "2026-11-16T04:28:57.6667057Z",
  "datacontenttype": "application/json",
  "partitionkey": "s25",
  "data": {
    "v": 1,
    "session": {
      "id": 25,
      "connectionId": "01a1e29d-89a1-7d18-a0a5-48f16a6c5dbb",
      "login": "oledb19",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:62229"
    },
    "clientSecurity": "Mandatory (TDS 7.4), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-89cc-7056-ab35-061206b5f68a",
  "source": "daffytee://DEV01:14361",
  "type": "request.batch",
  "time": "2026-11-16T04:28:57.6751899Z",
  "datacontenttype": "application/json",
  "partitionkey": "s25",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 1.067,
    "session": {
      "id": 25,
      "connectionId": "01a1e29d-89a1-7d18-a0a5-48f16a6c5dbb",
      "login": "oledb19",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:62229"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "bytes": 54,
      "packets": 1,
      "sql": "SELECT @@VERSION"
    },
    "response": {
      "status": "ok",
      "rowCount": 1,
      "done": "DONE",
      "doneStatus": "0x0010",
      "bytes": 430,
      "packets": 1,
      "firstByteMs": 1.067,
      "rowCounts": [
        1
      ],
      "resultSets": [
        1
      ]
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-89d8-72ad-bc36-f48861952baa",
  "source": "daffytee://DEV01:14361",
  "type": "session.end",
  "time": "2026-11-16T04:28:57.6882168Z",
  "datacontenttype": "application/json",
  "partitionkey": "s25",
  "data": {
    "v": 1,
    "durationMs": 54.4,
    "session": {
      "id": 25,
      "connectionId": "01a1e29d-89a1-7d18-a0a5-48f16a6c5dbb",
      "login": "oledb19",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:62229"
    },
    "requests": 1,
    "bytesFromClient": 62,
    "bytesFromTarget": 438,
    "reason": "client disconnected"
  }
}
```

</details>

### sqlcmd 15 (classic, on ODBC Driver 17)

**sqlcmd 15.0 (classic, on ODBC Driver 17)**

`SQLCMD.EXE -S tcp:127.0.0.1,14361 -U sqlcmd_odbc17 -N -C -Q "SELECT @@VERSION"`, with the password in `SQLCMDPASSWORD`. This is the sqlcmd in `C:\Program Files\Microsoft SQL Server\Client SDK\ODBC\170\Tools\Binn`. It is the ODBC 17 driver underneath (same feature extensions), but sqlcmd names itself `SQLCMD`, uses 4096-byte packets, and sends two `SET` batches of its own before the query.

The client printed (exit code 0):

```text
---------------------------------------- ...
Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64)
	Aug 26 2026 11:02:22
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>
(1 rows affected)
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:29:17.7306339Z","level":"debug","event":"session.accepted","session":46,"msg":"connection from 127.0.0.1:60499"}
{"ts":"2026-11-16T04:29:17.7360551Z","level":"debug","event":"session.login7","session":46,"msg":"LOGIN7 user='sqlcmd_odbc17' host='DEV01' app='SQLCMD' server='tcp:127.0.0.1,14361' db='' tds=7.4 packetSize=4096 features=[SQLDNSCACHING,DATACLASSIFICATION,UTF8SUPPORT,SESSIONRECOVERY] interface='ODBC' progVer=0x07000000 pid=31128 lcid=0x0409 tz=360 flags=E0-03-00-10 via Mandatory (TDS 7.4), Tls12"}
{"ts":"2026-11-16T04:29:17.7611409Z","level":"info","event":"session.open","session":46,"msg":"sqlcmd_odbc17 -> svc_app@127.0.0.1:14333 (spid 58, Microsoft SQL Server 16.0.4295) app='SQLCMD' host='DEV01' db='DaffyTeeTest' from 127.0.0.1:60499 [handshake 28 ms: client tls 2, auth 17, target login 7]"}
{"ts":"2026-11-16T04:29:17.7665055Z","level":"info","event":"session.close","session":46,"msg":"closed: client disconnected; 3 request(s), 206 B in, 517 B out, 00:00:00"}
```

**SQL log** (`--sql-log`, local time):

```sql
-- 2026-11-15 22:29:17.761  s46  sqlcmd_odbc17 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=SQLCMD  session.start  clientSecurity=Mandatory (TDS 7.4), Tls12 targetSecurity=Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 serverVersion=Microsoft SQL Server 16.0.4295 mars=False
-- 2026-11-15 22:29:17.762  s46#1  sqlcmd_odbc17 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=SQLCMD  0.83 ms  ok
SET QUOTED_IDENTIFIER OFF
GO
-- 2026-11-15 22:29:17.764  s46#2  sqlcmd_odbc17 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=SQLCMD  0.86 ms  ok
SET TEXTSIZE 4096
GO
-- 2026-11-15 22:29:17.765  s46#3  sqlcmd_odbc17 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=SQLCMD  0.69 ms  ok  1 row(s)
SELECT @@VERSION
GO
-- 2026-11-15 22:29:17.766  s46  sqlcmd_odbc17 -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=SQLCMD  session.end  requests=3 bytesFromClient=206 bytesFromTarget=517 reason=client disconnected
```

**Transcript** (`--transcript`; it starts after the login, so PRELOGIN and LOGIN7 are not in it):

```text
### session 46 login='sqlcmd_odbc17' -> 127.0.0.1:14333 app='SQLCMD' host='DEV01' from=127.0.0.1:60499
### target svc_app@127.0.0.1:14333 spid=58 version=Microsoft SQL Server 16.0.4295
### client side: Mandatory (TDS 7.4), Tls12; target side: Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256

=== CLIENT --> TARGET  SQL_BATCH  +0.1ms
  type=0x01 SQL_BATCH  packets=1  payload=72 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    SQL: SET QUOTED_IDENTIFIER OFF

=== TARGET --> CLIENT  TABULAR_RESULT  +0.9ms
  type=0x04 TABULAR_RESULT  packets=1  payload=34 bytes
    [0000] 0xE4 SESSIONSTATE  -- not decoded, remainder follows
    0000  10 00 00 00 01 00 00 00  01 00 09 00 60 01 14 FF  |............`...|
    0010  E7 FF FF 00 FD 00 00 FD  00 00 00 00 00 00 00 00  |................|
    0020  00                                                |.|

=== CLIENT --> TARGET  SQL_BATCH  +1.6ms
  type=0x01 SQL_BATCH  packets=1  payload=56 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    SQL: SET TEXTSIZE 4096

=== TARGET --> CLIENT  TABULAR_RESULT  +2.4ms
  type=0x04 TABULAR_RESULT  packets=1  payload=29 bytes
    [0000] 0xE4 SESSIONSTATE  -- not decoded, remainder follows
    0000  0B 00 00 00 02 00 00 00  01 09 04 00 10 00 00 FD  |................|
    0010  00 00 BE 00 00 00 00 00  00 00 00 00              |............|

=== CLIENT --> TARGET  SQL_BATCH  +2.6ms
  type=0x01 SQL_BATCH  packets=1  payload=54 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    SQL: SELECT @@VERSION

=== TARGET --> CLIENT  TABULAR_RESULT  +3.3ms
  type=0x04 TABULAR_RESULT  packets=1  payload=430 bytes
    [0000] 0x81 COLMETADATA   count=1
                       col[0]               NVARCHAR(600 bytes)  flags=0x0021 userType=0
    [0012] 0xD1 ROW           ="Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) 
	Aug 26 2026 11:02:22 
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>"
    [01A1] 0xFD DONE          status=0x0010 [COUNT] curcmd=0x00C1 rows=1

### closed: client disconnected
```

<details><summary><b>Events</b> (<code>--sink file</code>): 5 - session.start, request.batch, request.batch, request.batch, session.end</summary>

```json
{
  "specversion": "1.0",
  "id": "01a1e29d-d841-7f5e-8fb0-f480dfc46d50",
  "source": "daffytee://DEV01:14361",
  "type": "session.start",
  "time": "2026-11-16T04:29:17.7618493Z",
  "datacontenttype": "application/json",
  "partitionkey": "s46",
  "data": {
    "v": 1,
    "session": {
      "id": 46,
      "connectionId": "01a1e29d-d822-791c-b4d9-bfdbb0047b02",
      "login": "sqlcmd_odbc17",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "SQLCMD",
      "client": "127.0.0.1:60499"
    },
    "clientSecurity": "Mandatory (TDS 7.4), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-d844-77c5-a835-686449fda354",
  "source": "daffytee://DEV01:14361",
  "type": "request.batch",
  "time": "2026-11-16T04:29:17.762694Z",
  "datacontenttype": "application/json",
  "partitionkey": "s46",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 0.834,
    "session": {
      "id": 46,
      "connectionId": "01a1e29d-d822-791c-b4d9-bfdbb0047b02",
      "login": "sqlcmd_odbc17",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "SQLCMD",
      "client": "127.0.0.1:60499"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "bytes": 72,
      "packets": 1,
      "sql": "SET QUOTED_IDENTIFIER OFF"
    },
    "response": {
      "status": "ok",
      "done": "DONE",
      "doneStatus": "0x0000",
      "bytes": 34,
      "packets": 1,
      "firstByteMs": 0.834
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-d845-7b19-b4fe-fec6c4c920e9",
  "source": "daffytee://DEV01:14361",
  "type": "request.batch",
  "time": "2026-11-16T04:29:17.7641425Z",
  "datacontenttype": "application/json",
  "partitionkey": "s46",
  "data": {
    "v": 1,
    "seq": 2,
    "durationMs": 0.858,
    "session": {
      "id": 46,
      "connectionId": "01a1e29d-d822-791c-b4d9-bfdbb0047b02",
      "login": "sqlcmd_odbc17",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "SQLCMD",
      "client": "127.0.0.1:60499"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "bytes": 56,
      "packets": 1,
      "sql": "SET TEXTSIZE 4096"
    },
    "response": {
      "status": "ok",
      "done": "DONE",
      "doneStatus": "0x0000",
      "bytes": 29,
      "packets": 1,
      "firstByteMs": 0.858
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-d845-7b12-8178-c513301b525f",
  "source": "daffytee://DEV01:14361",
  "type": "request.batch",
  "time": "2026-11-16T04:29:17.7651718Z",
  "datacontenttype": "application/json",
  "partitionkey": "s46",
  "data": {
    "v": 1,
    "seq": 3,
    "durationMs": 0.688,
    "session": {
      "id": 46,
      "connectionId": "01a1e29d-d822-791c-b4d9-bfdbb0047b02",
      "login": "sqlcmd_odbc17",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "SQLCMD",
      "client": "127.0.0.1:60499"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "bytes": 54,
      "packets": 1,
      "sql": "SELECT @@VERSION"
    },
    "response": {
      "status": "ok",
      "rowCount": 1,
      "done": "DONE",
      "doneStatus": "0x0010",
      "bytes": 430,
      "packets": 1,
      "firstByteMs": 0.688,
      "rowCounts": [
        1
      ],
      "resultSets": [
        1
      ]
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-d846-7021-897a-71577755b478",
  "source": "daffytee://DEV01:14361",
  "type": "session.end",
  "time": "2026-11-16T04:29:17.7667599Z",
  "datacontenttype": "application/json",
  "partitionkey": "s46",
  "data": {
    "v": 1,
    "durationMs": 36.1,
    "session": {
      "id": 46,
      "connectionId": "01a1e29d-d822-791c-b4d9-bfdbb0047b02",
      "login": "sqlcmd_odbc17",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "SQLCMD",
      "client": "127.0.0.1:60499"
    },
    "requests": 3,
    "bytesFromClient": 206,
    "bytesFromTarget": 517,
    "reason": "client disconnected"
  }
}
```

</details>

### go-sqlcmd 1.10.0 (on go-mssqldb)

**go-sqlcmd (on go-mssqldb)**

`sqlcmd -S tcp:127.0.0.1,14361 -U go_sqlcmd -N true -C -Q "SELECT @@VERSION"`, run from `C:\Program Files\SqlCmd` (the `sqlcmd` first on this machine's PATH), with the password in `SQLCMDPASSWORD`. It is a separate TDS implementation in Go, not ODBC. It asks for no feature extensions, sends the host without the port as the server name, flags its first request as a connection reset, and ends with a TCP reset. Its LOGIN7 calls the library `go-mssqldb`.

The client printed (exit code 0):

```text
---------------------------------------- ...
Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64)
	Aug 26 2026 11:02:22
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>
(1 row affected)
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:29:19.3264017Z","level":"debug","event":"session.accepted","session":47,"msg":"connection from 127.0.0.1:60501"}
{"ts":"2026-11-16T04:29:19.3347125Z","level":"debug","event":"session.login7","session":47,"msg":"LOGIN7 user='go_sqlcmd' host='DEV01' app='sqlcmd' server='127.0.0.1' db='' tds=7.4 packetSize=4096 features=[] interface='go-mssqldb' progVer=0x01090008 pid=28952 lcid=0x0000 tz=0 flags=A0-02-00-00 via Mandatory (TDS 7.4), Tls12"}
{"ts":"2026-11-16T04:29:19.3595184Z","level":"info","event":"session.open","session":47,"msg":"go_sqlcmd -> svc_app@127.0.0.1:14333 (spid 58, Microsoft SQL Server 16.0.4295) app='sqlcmd' host='DEV01' db='DaffyTeeTest' from 127.0.0.1:60501 [handshake 32 ms: client tls 3, auth 16, target login 7]"}
{"ts":"2026-11-16T04:29:19.368978Z","level":"info","event":"session.close","session":47,"msg":"closed: client disconnected (An existing connection was forcibly closed by the remote host.); 1 request(s), 62 B in, 444 B out, 00:00:00"}
```

**SQL log** (`--sql-log`, local time):

```sql
-- 2026-11-15 22:29:19.360  s47  go_sqlcmd -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=sqlcmd  session.start  clientSecurity=Mandatory (TDS 7.4), Tls12 targetSecurity=Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 serverVersion=Microsoft SQL Server 16.0.4295 mars=False
-- 2026-11-15 22:29:19.361  s47#1  go_sqlcmd -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=sqlcmd  0.96 ms  ok  1 row(s)
-- (pooled connection reset before this request)
SELECT @@VERSION
GO
-- 2026-11-15 22:29:19.369  s47  go_sqlcmd -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=sqlcmd  session.end  requests=1 bytesFromClient=62 bytesFromTarget=444 reason=client disconnected (An existing connection was forcibly closed by the remote host.)
```

**Transcript** (`--transcript`; it starts after the login, so PRELOGIN and LOGIN7 are not in it):

```text
### session 47 login='go_sqlcmd' -> 127.0.0.1:14333 app='sqlcmd' host='DEV01' from=127.0.0.1:60501
### target svc_app@127.0.0.1:14333 spid=58 version=Microsoft SQL Server 16.0.4295
### client side: Mandatory (TDS 7.4), Tls12; target side: Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256

=== CLIENT --> TARGET  SQL_BATCH  +0.0ms
  type=0x01 SQL_BATCH  packets=1  payload=54 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    SQL: SELECT @@VERSION

=== TARGET --> CLIENT  TABULAR_RESULT  +1.0ms
  type=0x04 TABULAR_RESULT  packets=1  payload=436 bytes
    [0000] 0xE3 ENVCHANGE     type=18 00 00
    [0006] 0x81 COLMETADATA   count=1
                       col[0]               NVARCHAR(600 bytes)  flags=0x0021 userType=0
    [0018] 0xD1 ROW           ="Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) 
	Aug 26 2026 11:02:22 
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>"
    [01A7] 0xFD DONE          status=0x0010 [COUNT] curcmd=0x00C1 rows=1

### closed: client disconnected (An existing connection was forcibly closed by the remote host.)
```

<details><summary><b>Events</b> (<code>--sink file</code>): 3 - session.start, request.batch, session.end</summary>

```json
{
  "specversion": "1.0",
  "id": "01a1e29d-de80-7400-a0c4-23a4ad136a4b",
  "source": "daffytee://DEV01:14361",
  "type": "session.start",
  "time": "2026-11-16T04:29:19.3603009Z",
  "datacontenttype": "application/json",
  "partitionkey": "s47",
  "data": {
    "v": 1,
    "session": {
      "id": 47,
      "connectionId": "01a1e29d-de5e-70b9-b9f2-34796d5a5529",
      "login": "go_sqlcmd",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "sqlcmd",
      "client": "127.0.0.1:60501"
    },
    "clientSecurity": "Mandatory (TDS 7.4), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-de82-7176-b22c-425b8cbf558f",
  "source": "daffytee://DEV01:14361",
  "type": "request.batch",
  "time": "2026-11-16T04:29:19.3610985Z",
  "datacontenttype": "application/json",
  "partitionkey": "s47",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 0.964,
    "session": {
      "id": 47,
      "connectionId": "01a1e29d-de5e-70b9-b9f2-34796d5a5529",
      "login": "go_sqlcmd",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "sqlcmd",
      "client": "127.0.0.1:60501"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "resetConnection": true,
      "bytes": 54,
      "packets": 1,
      "sql": "SELECT @@VERSION"
    },
    "response": {
      "status": "ok",
      "rowCount": 1,
      "done": "DONE",
      "doneStatus": "0x0010",
      "bytes": 436,
      "packets": 1,
      "firstByteMs": 0.964,
      "rowCounts": [
        1
      ],
      "resultSets": [
        1
      ]
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-de89-7e34-be23-f94eb22c8644",
  "source": "daffytee://DEV01:14361",
  "type": "session.end",
  "time": "2026-11-16T04:29:19.3692611Z",
  "datacontenttype": "application/json",
  "partitionkey": "s47",
  "data": {
    "v": 1,
    "durationMs": 42.9,
    "session": {
      "id": 47,
      "connectionId": "01a1e29d-de5e-70b9-b9f2-34796d5a5529",
      "login": "go_sqlcmd",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "sqlcmd",
      "client": "127.0.0.1:60501"
    },
    "requests": 1,
    "bytesFromClient": 62,
    "bytesFromTarget": 444,
    "reason": "client disconnected (An existing connection was forcibly closed by the remote host.)"
  }
}
```

</details>

### Microsoft JDBC Driver 13.4.0

**Microsoft JDBC Driver 13.4.0. for SQL Server (Java 25.0.3)**

`DriverManager.getConnection("jdbc:sqlserver://127.0.0.1:14361;encrypt=true;trustServerCertificate=true", "jdbc", ...)`, then `executeQuery("SELECT @@VERSION")`. Run on DBeaver's Java runtime. It asks for eight feature extensions, including USERAGENT (0x10), which 0.3.1 logged only as `0x10`. Column encryption is negotiated, as with SqlClient.

The client printed (exit code 0):

```text
Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64)
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:29:21.0813886Z","level":"debug","event":"session.accepted","session":48,"msg":"connection from 127.0.0.1:60503"}
{"ts":"2026-11-16T04:29:21.1670504Z","level":"debug","event":"session.login7","session":48,"msg":"LOGIN7 user='jdbc' host='DEV01' app='Microsoft JDBC Driver for SQL Server' server='127.0.0.1' db='' tds=7.4 packetSize=8000 features=[USERAGENT,COLUMNENCRYPTION,DATACLASSIFICATION,UTF8SUPPORT,SQLDNSCACHING,VECTORSUPPORT,JSONSUPPORT,SESSIONRECOVERY] interface='Microsoft JDBC Driver 13.4' progVer=0x0D040000 pid=24800 lcid=0x0000 tz=0 flags=E0-03-00-18 via Mandatory (TDS 7.4), Tls12"}
{"ts":"2026-11-16T04:29:21.1942169Z","level":"info","event":"session.open","session":48,"msg":"jdbc -> svc_app@127.0.0.1:14333 (spid 58, Microsoft SQL Server 16.0.4295) app='Microsoft JDBC Driver for SQL Server' host='DEV01' db='DaffyTeeTest' from 127.0.0.1:60503 [handshake 110 ms: client tls 80, auth 17, target login 7]"}
{"ts":"2026-11-16T04:29:21.235653Z","level":"info","event":"session.close","session":48,"msg":"closed: client disconnected; 1 request(s), 62 B in, 440 B out, 00:00:00"}
```

**SQL log** (`--sql-log`, local time):

```sql
-- 2026-11-15 22:29:21.194  s48  jdbc -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft JDBC Driver for SQL Server  session.start  clientSecurity=Mandatory (TDS 7.4), Tls12 targetSecurity=Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 serverVersion=Microsoft SQL Server 16.0.4295 mars=False
-- 2026-11-15 22:29:21.206  s48#1  jdbc -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft JDBC Driver for SQL Server  1.28 ms  ok  1 row(s)
SELECT @@VERSION
GO
-- 2026-11-15 22:29:21.236  s48  jdbc -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft JDBC Driver for SQL Server  session.end  requests=1 bytesFromClient=62 bytesFromTarget=440 reason=client disconnected
```

**Transcript** (`--transcript`; it starts after the login, so PRELOGIN and LOGIN7 are not in it):

```text
### session 48 login='jdbc' -> 127.0.0.1:14333 app='Microsoft JDBC Driver for SQL Server' host='DEV01' from=127.0.0.1:60503
### target svc_app@127.0.0.1:14333 spid=58 version=Microsoft SQL Server 16.0.4295
### client side: Mandatory (TDS 7.4), Tls12; target side: Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256

=== CLIENT --> TARGET  SQL_BATCH  +10.9ms
  type=0x01 SQL_BATCH  packets=1  payload=54 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    SQL: SELECT @@VERSION

=== TARGET --> CLIENT  TABULAR_RESULT  +12.2ms
  type=0x04 TABULAR_RESULT  packets=1  payload=432 bytes
    [0000] 0x81 COLMETADATA   count=1 cekTable=0
                       col[0]               NVARCHAR(600 bytes)  flags=0x0021 userType=0
    [0014] 0xD1 ROW           ="Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) 
	Aug 26 2026 11:02:22 
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>"
    [01A3] 0xFD DONE          status=0x0010 [COUNT] curcmd=0x00C1 rows=1

### closed: client disconnected
```

<details><summary><b>Events</b> (<code>--sink file</code>): 3 - session.start, request.batch, session.end</summary>

```json
{
  "specversion": "1.0",
  "id": "01a1e29d-e5aa-7cdf-b26b-1cb1ef18da9b",
  "source": "daffytee://DEV01:14361",
  "type": "session.start",
  "time": "2026-11-16T04:29:21.1949281Z",
  "datacontenttype": "application/json",
  "partitionkey": "s48",
  "data": {
    "v": 1,
    "session": {
      "id": 48,
      "connectionId": "01a1e29d-e539-7897-b720-fcbdc93901b8",
      "login": "jdbc",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft JDBC Driver for SQL Server",
      "client": "127.0.0.1:60503"
    },
    "clientSecurity": "Mandatory (TDS 7.4), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-e5b8-79cd-8db1-82bacc30ddac",
  "source": "daffytee://DEV01:14361",
  "type": "request.batch",
  "time": "2026-11-16T04:29:21.2066229Z",
  "datacontenttype": "application/json",
  "partitionkey": "s48",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 1.28,
    "session": {
      "id": 48,
      "connectionId": "01a1e29d-e539-7897-b720-fcbdc93901b8",
      "login": "jdbc",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft JDBC Driver for SQL Server",
      "client": "127.0.0.1:60503"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "bytes": 54,
      "packets": 1,
      "sql": "SELECT @@VERSION"
    },
    "response": {
      "status": "ok",
      "rowCount": 1,
      "done": "DONE",
      "doneStatus": "0x0010",
      "bytes": 432,
      "packets": 1,
      "firstByteMs": 1.28,
      "rowCounts": [
        1
      ],
      "resultSets": [
        1
      ]
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-e5d4-79a3-986a-ef6bc341e9ae",
  "source": "daffytee://DEV01:14361",
  "type": "session.end",
  "time": "2026-11-16T04:29:21.2364002Z",
  "datacontenttype": "application/json",
  "partitionkey": "s48",
  "data": {
    "v": 1,
    "durationMs": 155,
    "session": {
      "id": 48,
      "connectionId": "01a1e29d-e539-7897-b720-fcbdc93901b8",
      "login": "jdbc",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft JDBC Driver for SQL Server",
      "client": "127.0.0.1:60503"
    },
    "requests": 1,
    "bytesFromClient": 62,
    "bytesFromTarget": 440,
    "reason": "client disconnected"
  }
}
```

</details>

### tedious 20.3.9 (Node.js)

**tedious 20.3.9 (Node.js v25.2.1)**

`new Connection({ server: '127.0.0.1', options: { port: 14361, encrypt: true, trustServerCertificate: true }, ... })`, then `execSql(new Request('SELECT @@VERSION'))`. It is the only client that sets up the session with SQL: one batch of fifteen `SET` statements straight after login. It then sends the query as `sp_executesql`, so DaffyTee records a `request.rpc`.

The client printed (exit code 0):

```text
Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64)
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:29:23.1186334Z","level":"debug","event":"session.accepted","session":49,"msg":"connection from 127.0.0.1:60505"}
{"ts":"2026-11-16T04:29:23.1358235Z","level":"debug","event":"session.login7","session":49,"msg":"LOGIN7 user='tedious' host='DEV01' app='Tedious' server='127.0.0.1' db='' tds=7.4 packetSize=4096 features=[UTF8SUPPORT] interface='Tedious' progVer=0x00000000 pid=39660 lcid=0x0409 tz=360 flags=F0-00-00-18 via Mandatory (TDS 7.4), Tls12"}
{"ts":"2026-11-16T04:29:23.1615389Z","level":"info","event":"session.open","session":49,"msg":"tedious -> svc_app@127.0.0.1:14333 (spid 58, Microsoft SQL Server 16.0.4295) app='Tedious' host='DEV01' db='DaffyTeeTest' from 127.0.0.1:60505 [handshake 40 ms: client tls 11, auth 16, target login 8]"}
{"ts":"2026-11-16T04:29:23.1727558Z","level":"info","event":"session.close","session":49,"msg":"closed: client disconnected; 2 request(s), 856 B in, 811 B out, 00:00:00"}
```

**SQL log** (`--sql-log`, local time):

```sql
-- 2026-11-15 22:29:23.162  s49  tedious -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Tedious  session.start  clientSecurity=Mandatory (TDS 7.4), Tls12 targetSecurity=Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 serverVersion=Microsoft SQL Server 16.0.4295 mars=False
-- 2026-11-15 22:29:23.164  s49#1  tedious -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Tedious  1.95 ms  ok
set ansi_nulls on
set ansi_null_dflt_on on
set ansi_padding on
set ansi_warnings on
set arithabort on
set concat_null_yields_null on
set datefirst 7
set dateformat mdy
set implicit_transactions off
set language us_english
set numeric_roundabort off
set quoted_identifier on
set textsize 2147483647
set transaction isolation level read committed
set xact_abort off
GO
-- 2026-11-15 22:29:23.169  s49#2  tedious -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Tedious  3.98 ms  ok
EXEC sp_executesql N'SELECT @@VERSION';
GO
-- 2026-11-15 22:29:23.173  s49  tedious -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Tedious  session.end  requests=2 bytesFromClient=856 bytesFromTarget=811 reason=client disconnected
```

**Transcript** (`--transcript`; it starts after the login, so PRELOGIN and LOGIN7 are not in it):

```text
### session 49 login='tedious' -> 127.0.0.1:14333 app='Tedious' host='DEV01' from=127.0.0.1:60505
### target svc_app@127.0.0.1:14333 spid=58 version=Microsoft SQL Server 16.0.4295
### client side: Mandatory (TDS 7.4), Tls12; target side: Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256

=== CLIENT --> TARGET  SQL_BATCH  +0.3ms
  type=0x01 SQL_BATCH  packets=1  payload=748 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    SQL: set ansi_nulls on
         set ansi_null_dflt_on on
         set ansi_padding on
         set ansi_warnings on
         set arithabort on
         set concat_null_yields_null on
         set datefirst 7
         set dateformat mdy
         set implicit_transactions off
         set language us_english
         set numeric_roundabort off
         set quoted_identifier on
         set textsize 2147483647
         set transaction isolation level read committed
         set xact_abort off

=== TARGET --> CLIENT  TABULAR_RESULT  +2.2ms
  type=0x04 TABULAR_RESULT  packets=1  payload=347 bytes
    [0000] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00B9 rows=0
    [000D] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00B9 rows=0
    [001A] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00B9 rows=0
    [0027] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00B9 rows=0
    [0034] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00B9 rows=0
    [0041] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00B9 rows=0
    [004E] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00F9 rows=0
    [005B] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00F9 rows=0
    [0068] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00BA rows=0
    [0075] 0xE3 ENVCHANGE     type=2 "us_english" (was "us_english")
    [00A3] 0xAB INFO          num=5703 state=1 class=0 "Changed language setting to us_english."
    [011A] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00F9 rows=0
    [0127] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00BA rows=0
    [0134] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00BE rows=0
    [0141] 0xFD DONE          status=0x0001 [MORE] curcmd=0x00F9 rows=0
    [014E] 0xFD DONE          status=0x0000 [FINAL] curcmd=0x00BA rows=0

=== CLIENT --> TARGET  RPC  +3.6ms
  type=0x03 RPC  packets=1  payload=92 bytes
    ALL_HEADERS: 22 bytes
      type=0x0002 TRANSACTION_DESCRIPTOR  00 00 00 00 00 00 00 00 01 00 00 00
    RPC: sp_executesql
    SQL: SELECT @@VERSION

=== TARGET --> CLIENT  TABULAR_RESULT  +7.5ms
  type=0x04 TABULAR_RESULT  packets=1  payload=448 bytes
    [0000] 0x81 COLMETADATA   count=1
                       col[0]               NVARCHAR(600 bytes)  flags=0x0021 userType=0
    [0012] 0xD1 ROW           ="Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) 
	Aug 26 2026 11:02:22 
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>"
    [01A1] 0xFF DONEINPROC    status=0x0011 [MORE|COUNT] curcmd=0x00C1 rows=1
    [01AE] 0x79 RETURNSTATUS  value=0
    [01B3] 0xFE DONEPROC      status=0x0000 [FINAL] curcmd=0x00E0 rows=0

### closed: client disconnected
```

<details><summary><b>Events</b> (<code>--sink file</code>): 4 - session.start, request.batch, request.rpc, session.end</summary>

```json
{
  "specversion": "1.0",
  "id": "01a1e29d-ed5a-7975-b565-7f94a33d0d8b",
  "source": "daffytee://DEV01:14361",
  "type": "session.start",
  "time": "2026-11-16T04:29:23.1624089Z",
  "datacontenttype": "application/json",
  "partitionkey": "s49",
  "data": {
    "v": 1,
    "session": {
      "id": 49,
      "connectionId": "01a1e29d-ed2e-7b4c-9951-f07000e85d23",
      "login": "tedious",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Tedious",
      "client": "127.0.0.1:60505"
    },
    "clientSecurity": "Mandatory (TDS 7.4), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-ed5e-7212-838f-1835588ad65c",
  "source": "daffytee://DEV01:14361",
  "type": "request.batch",
  "time": "2026-11-16T04:29:23.1641651Z",
  "datacontenttype": "application/json",
  "partitionkey": "s49",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 1.948,
    "session": {
      "id": 49,
      "connectionId": "01a1e29d-ed2e-7b4c-9951-f07000e85d23",
      "login": "tedious",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Tedious",
      "client": "127.0.0.1:60505"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "bytes": 748,
      "packets": 1,
      "sql": "set ansi_nulls on\nset ansi_null_dflt_on on\nset ansi_padding on\nset ansi_warnings on\nset arithabort on\nset concat_null_yields_null on\nset datefirst 7\nset dateformat mdy\nset implicit_transactions off\nset language us_english\nset numeric_roundabort off\nset quoted_identifier on\nset textsize 2147483647\nset transaction isolation level read committed\nset xact_abort off"
    },
    "response": {
      "status": "ok",
      "done": "DONE",
      "doneStatus": "0x0000",
      "bytes": 347,
      "packets": 1,
      "firstByteMs": 1.948,
      "messages": [
        {
          "number": 5703,
          "severity": 0,
          "state": 1,
          "message": "Changed language setting to us_english.",
          "line": 10
        }
      ]
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-ed63-7844-a9a1-cb0c403ef757",
  "source": "daffytee://DEV01:14361",
  "type": "request.rpc",
  "time": "2026-11-16T04:29:23.1698712Z",
  "datacontenttype": "application/json",
  "partitionkey": "s49",
  "data": {
    "v": 1,
    "seq": 2,
    "durationMs": 3.975,
    "session": {
      "id": 49,
      "connectionId": "01a1e29d-ed2e-7b4c-9951-f07000e85d23",
      "login": "tedious",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Tedious",
      "client": "127.0.0.1:60505"
    },
    "request": {
      "kind": "rpc",
      "txn": "0x0000000000000000",
      "bytes": 92,
      "packets": 1,
      "calls": [
        {
          "proc": "sp_executesql",
          "procId": 10,
          "sql": "SELECT @@VERSION",
          "params": []
        }
      ]
    },
    "response": {
      "status": "ok",
      "done": "DONEPROC",
      "doneStatus": "0x0000",
      "bytes": 448,
      "packets": 1,
      "firstByteMs": 3.975,
      "rowCounts": [
        1
      ],
      "resultSets": [
        1
      ],
      "returnStatus": 0
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29d-ed65-7424-be21-0c1a557b0734",
  "source": "daffytee://DEV01:14361",
  "type": "session.end",
  "time": "2026-11-16T04:29:23.1734666Z",
  "datacontenttype": "application/json",
  "partitionkey": "s49",
  "data": {
    "v": 1,
    "durationMs": 54.9,
    "session": {
      "id": 49,
      "connectionId": "01a1e29d-ed2e-7b4c-9951-f07000e85d23",
      "login": "tedious",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 58,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Tedious",
      "client": "127.0.0.1:60505"
    },
    "requests": 2,
    "bytesFromClient": 856,
    "bytesFromTarget": 811,
    "reason": "client disconnected"
  }
}
```

</details>

### SQLSRV32 ODBC driver (ships with Windows), encryption on

**SQL Server ODBC driver (SQLSRV32, ships with Windows)**

`Driver={SQL Server};Server=tcp:127.0.0.1,14361;Uid=odbc_legacy;Pwd=...;Encrypt=yes;TrustServerCertificate=yes`, through `System.Data.Odbc`. The driver does not know `TrustServerCertificate` (it says "Invalid connection string attribute"), checks DaffyTee's self-signed certificate, and hangs up after the TLS handshake with "SSL Security error". It retries about 20 times. Each attempt is now logged as `session.closed_early` with the reason (finding 2); 0.3.1 logged only `session.accepted`.

The client printed (exit code 1):

```text
ERROR [08001] [Microsoft][ODBC SQL Server Driver][DBNETLIB]SSL Security error
ERROR [01000] [Microsoft][ODBC SQL Server Driver][DBNETLIB]ConnectionOpen (SECDoClientHandshake()).
ERROR [01S00] [Microsoft][ODBC SQL Server Driver]Invalid connection string attribute
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:28:40.1089123Z","level":"debug","event":"session.accepted","session":6,"msg":"connection from 127.0.0.1:62209"}
{"ts":"2026-11-16T04:28:40.1168455Z","level":"debug","event":"session.closed_early","session":6,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:40.2306652Z","level":"debug","event":"session.accepted","session":7,"msg":"connection from 127.0.0.1:62210"}
{"ts":"2026-11-16T04:28:40.2370573Z","level":"debug","event":"session.closed_early","session":7,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:40.4494415Z","level":"debug","event":"session.accepted","session":8,"msg":"connection from 127.0.0.1:62211"}
{"ts":"2026-11-16T04:28:40.4547065Z","level":"debug","event":"session.closed_early","session":8,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:40.8577528Z","level":"debug","event":"session.accepted","session":9,"msg":"connection from 127.0.0.1:62212"}
{"ts":"2026-11-16T04:28:40.8642666Z","level":"debug","event":"session.closed_early","session":9,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:41.6712993Z","level":"debug","event":"session.accepted","session":10,"msg":"connection from 127.0.0.1:62213"}
{"ts":"2026-11-16T04:28:41.6773599Z","level":"debug","event":"session.closed_early","session":10,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:42.6910462Z","level":"debug","event":"session.accepted","session":11,"msg":"connection from 127.0.0.1:62214"}
{"ts":"2026-11-16T04:28:42.6971191Z","level":"debug","event":"session.closed_early","session":11,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:43.7114549Z","level":"debug","event":"session.accepted","session":12,"msg":"connection from 127.0.0.1:62215"}
{"ts":"2026-11-16T04:28:43.7170699Z","level":"debug","event":"session.closed_early","session":12,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:44.7195673Z","level":"debug","event":"session.accepted","session":13,"msg":"connection from 127.0.0.1:62216"}
{"ts":"2026-11-16T04:28:44.7249358Z","level":"debug","event":"session.closed_early","session":13,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:45.7257786Z","level":"debug","event":"session.accepted","session":14,"msg":"connection from 127.0.0.1:62217"}
{"ts":"2026-11-16T04:28:45.7315256Z","level":"debug","event":"session.closed_early","session":14,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:46.7439306Z","level":"debug","event":"session.accepted","session":15,"msg":"connection from 127.0.0.1:62218"}
{"ts":"2026-11-16T04:28:46.7501495Z","level":"debug","event":"session.closed_early","session":15,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:47.7583882Z","level":"debug","event":"session.accepted","session":16,"msg":"connection from 127.0.0.1:62219"}
{"ts":"2026-11-16T04:28:47.7637846Z","level":"debug","event":"session.closed_early","session":16,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:48.7796898Z","level":"debug","event":"session.accepted","session":17,"msg":"connection from 127.0.0.1:62221"}
{"ts":"2026-11-16T04:28:48.7864647Z","level":"debug","event":"session.closed_early","session":17,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:49.7992472Z","level":"debug","event":"session.accepted","session":18,"msg":"connection from 127.0.0.1:62222"}
{"ts":"2026-11-16T04:28:49.8061431Z","level":"debug","event":"session.closed_early","session":18,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:50.8217432Z","level":"debug","event":"session.accepted","session":19,"msg":"connection from 127.0.0.1:62223"}
{"ts":"2026-11-16T04:28:50.8278718Z","level":"debug","event":"session.closed_early","session":19,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:51.8419994Z","level":"debug","event":"session.accepted","session":20,"msg":"connection from 127.0.0.1:62224"}
{"ts":"2026-11-16T04:28:51.847328Z","level":"debug","event":"session.closed_early","session":20,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:52.8629304Z","level":"debug","event":"session.accepted","session":21,"msg":"connection from 127.0.0.1:62225"}
{"ts":"2026-11-16T04:28:52.8690881Z","level":"debug","event":"session.closed_early","session":21,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:53.8833152Z","level":"debug","event":"session.accepted","session":22,"msg":"connection from 127.0.0.1:62226"}
{"ts":"2026-11-16T04:28:53.8888013Z","level":"debug","event":"session.closed_early","session":22,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:54.9028284Z","level":"debug","event":"session.accepted","session":23,"msg":"connection from 127.0.0.1:62227"}
{"ts":"2026-11-16T04:28:54.9086787Z","level":"debug","event":"session.closed_early","session":23,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:55.9237739Z","level":"debug","event":"session.accepted","session":24,"msg":"connection from 127.0.0.1:62228"}
{"ts":"2026-11-16T04:28:55.9300962Z","level":"debug","event":"session.closed_early","session":24,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
```

**SQL log** (`--sql-log`): nothing - no session started and no login was refused.

**Transcript** (`--transcript`): none - a transcript starts only once a session is open.

**Events** (`--sink file`): none.

### SQLSRV32 ODBC driver (ships with Windows), Encrypt=no

**SQL Server ODBC driver (SQLSRV32, ships with Windows), Encrypt=no (DaffyTee requires encryption)**

Connection string: `Driver={SQL Server};Server=tcp:127.0.0.1,14362;Uid=odbc_legacy;Pwd=...;Encrypt=no`, against DaffyTee's defaults. DaffyTee answers PRELOGIN with "encryption required", and the driver encrypts the whole session without checking the certificate: `Optional->upgraded (TDS 7.1), Tls12`. The LOGIN7 is TDS 7.1 with no feature extensions; 0.3.1 refused it as a password change (finding 1). The request has no `ALL_HEADERS`, and the reply's `DONE` has a 4-byte row count, which DaffyTee now reads.

The client printed (exit code 0):

```text
Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64)
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:29:29.8066684Z","level":"debug","event":"session.accepted","session":2,"msg":"connection from 127.0.0.1:60513"}
{"ts":"2026-11-16T04:29:29.8456468Z","level":"debug","event":"session.login7","session":2,"msg":"LOGIN7 user='odbc_legacy' host='DEV01' app='Microsoft® Windows® Operating System' server='tcp:127.0.0.1,14362' db='' tds=7.1 packetSize=0 features=[] interface='ODBC' progVer=0x07000000 pid=45920 lcid=0x0409 tz=360 flags=E0-03-00-08 via Optional->upgraded (TDS 7.1), Tls12"}
{"ts":"2026-11-16T04:29:29.8925479Z","level":"info","event":"session.open","session":2,"msg":"odbc_legacy -> svc_app@127.0.0.1:14333 (spid 60, Microsoft SQL Server 16.0.4295) app='Microsoft® Windows® Operating System' host='DEV01' db='DaffyTeeTest' from 127.0.0.1:60513 [handshake 82 ms: client tls 33, auth 20, target login 17]"}
{"ts":"2026-11-16T04:29:29.922733Z","level":"info","event":"session.close","session":2,"msg":"closed: client disconnected; 1 request(s), 40 B in, 432 B out, 00:00:00"}
```

**SQL log** (`--sql-log`, local time):

```sql
-- 2026-11-15 22:29:29.894  s2  odbc_legacy -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  session.start  clientSecurity=Optional->upgraded (TDS 7.1), Tls12 targetSecurity=Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 serverVersion=Microsoft SQL Server 16.0.4295 mars=False
-- 2026-11-15 22:29:29.902  s2#1  odbc_legacy -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  4.13 ms  ok  1 row(s)
SELECT @@VERSION
GO
-- 2026-11-15 22:29:29.923  s2  odbc_legacy -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  session.end  requests=1 bytesFromClient=40 bytesFromTarget=432 reason=client disconnected
```

**Transcript** (`--transcript`; it starts after the login, so PRELOGIN and LOGIN7 are not in it):

```text
### session 2 login='odbc_legacy' -> 127.0.0.1:14333 app='Microsoft® Windows® Operating System' host='DEV01' from=127.0.0.1:60513
### target svc_app@127.0.0.1:14333 spid=60 version=Microsoft SQL Server 16.0.4295
### client side: Optional->upgraded (TDS 7.1), Tls12; target side: Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256

=== CLIENT --> TARGET  SQL_BATCH  +4.4ms
  type=0x01 SQL_BATCH  packets=1  payload=32 bytes
    ALL_HEADERS: absent
    SQL: SELECT @@VERSION

=== TARGET --> CLIENT  TABULAR_RESULT  +8.5ms
  type=0x04 TABULAR_RESULT  packets=1  payload=424 bytes
    [0000] 0x81 COLMETADATA   count=1
                       col[0]               NVARCHAR(600 bytes)  flags=0x0021 userType=0
    [0010] 0xD1 ROW           ="Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) 
	Aug 26 2026 11:02:22 
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>"
    [019F] 0xFD DONE          status=0x0010 [COUNT] curcmd=0x00C1 rows=1

### closed: client disconnected
```

<details><summary><b>Events</b> (<code>--sink file</code>): 3 - session.start, request.batch, session.end</summary>

```json
{
  "specversion": "1.0",
  "id": "01a1e29e-07a6-7d86-a4df-3cc193eb45eb",
  "source": "daffytee://DEV01:14362",
  "type": "session.start",
  "time": "2026-11-16T04:29:29.8941568Z",
  "datacontenttype": "application/json",
  "partitionkey": "s2",
  "data": {
    "v": 1,
    "session": {
      "id": 2,
      "connectionId": "01a1e29e-074e-7ce2-91b2-aa48acb8650a",
      "login": "odbc_legacy",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 60,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:60513"
    },
    "clientSecurity": "Optional->upgraded (TDS 7.1), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29e-07c0-77f7-95c5-8d4086f2e257",
  "source": "daffytee://DEV01:14362",
  "type": "request.batch",
  "time": "2026-11-16T04:29:29.9029845Z",
  "datacontenttype": "application/json",
  "partitionkey": "s2",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 4.134,
    "session": {
      "id": 2,
      "connectionId": "01a1e29e-074e-7ce2-91b2-aa48acb8650a",
      "login": "odbc_legacy",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 60,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:60513"
    },
    "request": {
      "kind": "batch",
      "bytes": 32,
      "packets": 1,
      "sql": "SELECT @@VERSION"
    },
    "response": {
      "status": "ok",
      "rowCount": 1,
      "done": "DONE",
      "doneStatus": "0x0010",
      "bytes": 424,
      "packets": 1,
      "firstByteMs": 4.134,
      "rowCounts": [
        1
      ],
      "resultSets": [
        1
      ]
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29e-07c3-7717-89a9-4f7a42576550",
  "source": "daffytee://DEV01:14362",
  "type": "session.end",
  "time": "2026-11-16T04:29:29.9237124Z",
  "datacontenttype": "application/json",
  "partitionkey": "s2",
  "data": {
    "v": 1,
    "durationMs": 117.1,
    "session": {
      "id": 2,
      "connectionId": "01a1e29e-074e-7ce2-91b2-aa48acb8650a",
      "login": "odbc_legacy",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 60,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:60513"
    },
    "requests": 1,
    "bytesFromClient": 40,
    "bytesFromTarget": 432,
    "reason": "client disconnected"
  }
}
```

</details>

### SQLOLEDB provider (ships with Windows), encryption on

**SQLOLEDB provider (ships with Windows, System.Data.OleDb)**

`Provider=SQLOLEDB;Data Source=tcp:127.0.0.1,14361;User ID=oledb_legacy;Password=...;Use Encryption for Data=True;Trust Server Certificate=True`, through `System.Data.OleDb`. The same as the SQLSRV32 ODBC driver: "SSL Security error", about 20 attempts, each now logged with the reason.

The client printed (exit code 1):

```text
[DBNETLIB][ConnectionOpen (SECDoClientHandshake()).]SSL Security error.
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:28:59.3720234Z","level":"debug","event":"session.accepted","session":26,"msg":"connection from 127.0.0.1:62231"}
{"ts":"2026-11-16T04:28:59.379843Z","level":"debug","event":"session.closed_early","session":26,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:59.4860755Z","level":"debug","event":"session.accepted","session":27,"msg":"connection from 127.0.0.1:62232"}
{"ts":"2026-11-16T04:28:59.4896381Z","level":"debug","event":"session.closed_early","session":27,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:28:59.6905566Z","level":"debug","event":"session.accepted","session":28,"msg":"connection from 127.0.0.1:62233"}
{"ts":"2026-11-16T04:28:59.6969878Z","level":"debug","event":"session.closed_early","session":28,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:00.1037402Z","level":"debug","event":"session.accepted","session":29,"msg":"connection from 127.0.0.1:62234"}
{"ts":"2026-11-16T04:29:00.1089845Z","level":"debug","event":"session.closed_early","session":29,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:00.9218543Z","level":"debug","event":"session.accepted","session":30,"msg":"connection from 127.0.0.1:62235"}
{"ts":"2026-11-16T04:29:00.9272844Z","level":"debug","event":"session.closed_early","session":30,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:01.9402436Z","level":"debug","event":"session.accepted","session":31,"msg":"connection from 127.0.0.1:62236"}
{"ts":"2026-11-16T04:29:01.9462186Z","level":"debug","event":"session.closed_early","session":31,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:02.9609918Z","level":"debug","event":"session.accepted","session":32,"msg":"connection from 127.0.0.1:62237"}
{"ts":"2026-11-16T04:29:02.9664895Z","level":"debug","event":"session.closed_early","session":32,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:03.9779864Z","level":"debug","event":"session.accepted","session":33,"msg":"connection from 127.0.0.1:62241"}
{"ts":"2026-11-16T04:29:03.9838141Z","level":"debug","event":"session.closed_early","session":33,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:04.9883134Z","level":"debug","event":"session.accepted","session":34,"msg":"connection from 127.0.0.1:62242"}
{"ts":"2026-11-16T04:29:04.993865Z","level":"debug","event":"session.closed_early","session":34,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:05.9952778Z","level":"debug","event":"session.accepted","session":35,"msg":"connection from 127.0.0.1:62243"}
{"ts":"2026-11-16T04:29:06.0016093Z","level":"debug","event":"session.closed_early","session":35,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:07.0161792Z","level":"debug","event":"session.accepted","session":36,"msg":"connection from 127.0.0.1:62244"}
{"ts":"2026-11-16T04:29:07.0221796Z","level":"debug","event":"session.closed_early","session":36,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:08.031537Z","level":"debug","event":"session.accepted","session":37,"msg":"connection from 127.0.0.1:62245"}
{"ts":"2026-11-16T04:29:08.037241Z","level":"debug","event":"session.closed_early","session":37,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:09.0525034Z","level":"debug","event":"session.accepted","session":38,"msg":"connection from 127.0.0.1:62246"}
{"ts":"2026-11-16T04:29:09.0572834Z","level":"debug","event":"session.closed_early","session":38,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:10.0615262Z","level":"debug","event":"session.accepted","session":39,"msg":"connection from 127.0.0.1:62247"}
{"ts":"2026-11-16T04:29:10.0661883Z","level":"debug","event":"session.closed_early","session":39,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:11.0794732Z","level":"debug","event":"session.accepted","session":40,"msg":"connection from 127.0.0.1:62248"}
{"ts":"2026-11-16T04:29:11.0856746Z","level":"debug","event":"session.closed_early","session":40,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:12.0914689Z","level":"debug","event":"session.accepted","session":41,"msg":"connection from 127.0.0.1:62249"}
{"ts":"2026-11-16T04:29:12.0980079Z","level":"debug","event":"session.closed_early","session":41,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:13.1135572Z","level":"debug","event":"session.accepted","session":42,"msg":"connection from 127.0.0.1:62250"}
{"ts":"2026-11-16T04:29:13.1179254Z","level":"debug","event":"session.closed_early","session":42,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:14.1193082Z","level":"debug","event":"session.accepted","session":43,"msg":"connection from 127.0.0.1:62251"}
{"ts":"2026-11-16T04:29:14.1259262Z","level":"debug","event":"session.closed_early","session":43,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:15.1396174Z","level":"debug","event":"session.accepted","session":44,"msg":"connection from 127.0.0.1:62252"}
{"ts":"2026-11-16T04:29:15.1461473Z","level":"debug","event":"session.closed_early","session":44,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
{"ts":"2026-11-16T04:29:16.15988Z","level":"debug","event":"session.accepted","session":45,"msg":"connection from 127.0.0.1:62253"}
{"ts":"2026-11-16T04:29:16.1654952Z","level":"debug","event":"session.closed_early","session":45,"msg":"client closed the connection after the TLS handshake, without logging in - a client that checks certificates does this when it does not trust DaffyTee's (see 'daffytee cert')"}
```

**SQL log** (`--sql-log`): nothing - no session started and no login was refused.

**Transcript** (`--transcript`): none - a transcript starts only once a session is open.

**Events** (`--sink file`): none.

### SQLOLEDB provider (ships with Windows), Encrypt=no

**SQLOLEDB provider (ships with Windows, System.Data.OleDb), Encrypt=no (DaffyTee requires encryption)**

Connection string: `Provider=SQLOLEDB;Data Source=tcp:127.0.0.1,14362;User ID=oledb_legacy;Password=...;Use Encryption for Data=False`, against DaffyTee's defaults. Encrypted all the same, because DaffyTee requires it. A TDS 7.1 LOGIN7 with 4096-byte packets and `fOLEDB` in TypeFlags (`flags=E0-03-10-08`).

The client printed (exit code 0):

```text
Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64)
```

**Operational log** (`--log-file`, UTC; LOGIN7 is the `session.login7` line):

```json
{"ts":"2026-11-16T04:29:31.6126659Z","level":"debug","event":"session.accepted","session":3,"msg":"connection from 127.0.0.1:63663"}
{"ts":"2026-11-16T04:29:31.6197285Z","level":"debug","event":"session.login7","session":3,"msg":"LOGIN7 user='oledb_legacy' host='DEV01' app='Microsoft® Windows® Operating System' server='tcp:127.0.0.1,14362' db='' tds=7.1 packetSize=4096 features=[] interface='OLEDB' progVer=0x07000000 pid=46364 lcid=0x0409 tz=360 flags=E0-03-10-08 via Optional->upgraded (TDS 7.1), Tls12"}
{"ts":"2026-11-16T04:29:31.646155Z","level":"info","event":"session.open","session":3,"msg":"oledb_legacy -> svc_app@127.0.0.1:14333 (spid 60, Microsoft SQL Server 16.0.4295) app='Microsoft® Windows® Operating System' host='DEV01' db='DaffyTeeTest' from 127.0.0.1:63663 [handshake 30 ms: client tls 3, auth 17, target login 8]"}
{"ts":"2026-11-16T04:29:31.6687067Z","level":"info","event":"session.close","session":3,"msg":"closed: client disconnected; 1 request(s), 40 B in, 432 B out, 00:00:00"}
```

**SQL log** (`--sql-log`, local time):

```sql
-- 2026-11-15 22:29:31.646  s3  oledb_legacy -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  session.start  clientSecurity=Optional->upgraded (TDS 7.1), Tls12 targetSecurity=Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256 serverVersion=Microsoft SQL Server 16.0.4295 mars=False
-- 2026-11-15 22:29:31.654  s3#1  oledb_legacy -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  1.27 ms  ok  1 row(s)
SELECT @@VERSION
GO
-- 2026-11-15 22:29:31.669  s3  oledb_legacy -> svc_app@127.0.0.1:14333/DaffyTeeTest  app=Microsoft® Windows® Operating System  session.end  requests=1 bytesFromClient=40 bytesFromTarget=432 reason=client disconnected
```

**Transcript** (`--transcript`; it starts after the login, so PRELOGIN and LOGIN7 are not in it):

```text
### session 3 login='oledb_legacy' -> 127.0.0.1:14333 app='Microsoft® Windows® Operating System' host='DEV01' from=127.0.0.1:63663
### target svc_app@127.0.0.1:14333 spid=60 version=Microsoft SQL Server 16.0.4295
### client side: Optional->upgraded (TDS 7.1), Tls12; target side: Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256

=== CLIENT --> TARGET  SQL_BATCH  +7.2ms
  type=0x01 SQL_BATCH  packets=1  payload=32 bytes
    ALL_HEADERS: absent
    SQL: SELECT @@VERSION

=== TARGET --> CLIENT  TABULAR_RESULT  +8.5ms
  type=0x04 TABULAR_RESULT  packets=1  payload=424 bytes
    [0000] 0x81 COLMETADATA   count=1
                       col[0]               NVARCHAR(600 bytes)  flags=0x0021 userType=0
    [0010] 0xD1 ROW           ="Microsoft SQL Server 2022 (RTM-CU27) (KB5104824) - 16.0.4295.3 (X64) 
	Aug 26 2026 11:02:22 
	Copyright (C) 2022 Microsoft Corporation
	Developer Edition (64-bit) on Linux (Ubuntu 22.04.5 LTS) <X64>"
    [019F] 0xFD DONE          status=0x0010 [COUNT] curcmd=0x00C1 rows=1

### closed: client disconnected
```

<details><summary><b>Events</b> (<code>--sink file</code>): 3 - session.start, request.batch, session.end</summary>

```json
{
  "specversion": "1.0",
  "id": "01a1e29e-0e7e-7036-815f-8e57cb756b20",
  "source": "daffytee://DEV01:14362",
  "type": "session.start",
  "time": "2026-11-16T04:29:31.6469074Z",
  "datacontenttype": "application/json",
  "partitionkey": "s3",
  "data": {
    "v": 1,
    "session": {
      "id": 3,
      "connectionId": "01a1e29e-0e5c-7cd2-adec-92475fc853a3",
      "login": "oledb_legacy",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 60,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:63663"
    },
    "clientSecurity": "Optional->upgraded (TDS 7.1), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29e-0e89-7a6f-b07e-ea01d6faf011",
  "source": "daffytee://DEV01:14362",
  "type": "request.batch",
  "time": "2026-11-16T04:29:31.6549722Z",
  "datacontenttype": "application/json",
  "partitionkey": "s3",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 1.269,
    "session": {
      "id": 3,
      "connectionId": "01a1e29e-0e5c-7cd2-adec-92475fc853a3",
      "login": "oledb_legacy",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 60,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:63663"
    },
    "request": {
      "kind": "batch",
      "bytes": 32,
      "packets": 1,
      "sql": "SELECT @@VERSION"
    },
    "response": {
      "status": "ok",
      "rowCount": 1,
      "done": "DONE",
      "doneStatus": "0x0010",
      "bytes": 424,
      "packets": 1,
      "firstByteMs": 1.269,
      "rowCounts": [
        1
      ],
      "resultSets": [
        1
      ]
    }
  }
}
{
  "specversion": "1.0",
  "id": "01a1e29e-0e95-7c9d-b992-ca99da837b0c",
  "source": "daffytee://DEV01:14362",
  "type": "session.end",
  "time": "2026-11-16T04:29:31.6695633Z",
  "datacontenttype": "application/json",
  "partitionkey": "s3",
  "data": {
    "v": 1,
    "durationMs": 56.9,
    "session": {
      "id": 3,
      "connectionId": "01a1e29e-0e5c-7cd2-adec-92475fc853a3",
      "login": "oledb_legacy",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 60,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "Microsoft® Windows® Operating System",
      "client": "127.0.0.1:63663"
    },
    "requests": 1,
    "bytesFromClient": 40,
    "bytesFromTarget": 432,
    "reason": "client disconnected"
  }
}
```

</details>
