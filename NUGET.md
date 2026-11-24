# DaffyTee

DaffyTee is a database gateway that lets existing applications reach SQL Server and Azure SQL
through an identity you control, and records everything they do - without changing the
application or the database.

It terminates each client connection, authenticates the caller with its own DaffyTee login, and
logs in to the database independently, as the identity mapped to that caller: a SQL login, or an
Entra ID application for Azure SQL - so an application that only knows SQL logins can reach a
database that expects Entra ID. It follows Azure SQL's redirects itself, so no session goes around
it. Every request is decoded and published asynchronously, as correlated events, to RabbitMQ or a
file, for governance, auditing and analytics.

Applications and tools connect to DaffyTee exactly as they would to SQL Server; only the
connection string points somewhere new. There is no code to change, and nothing to install or
switch on in the database - no agent, no trace, no Extended Events session.

- **Control** - each user gets their own DaffyTee login, mapped to a database identity they never
  see or hold. Refuse a user, or end a single session, without touching the database - from
  DaffyTee's prompt, or in T-SQL from SSMS with `EXEC daffytee.help`. Connections are encrypted by
  default.
- **Visibility** - watch every request as it happens: the SQL, the procedure and its parameter
  values, how long it took, the rows it returned or changed, and any error - live as runnable
  T-SQL, or written to a SQL log file.
- **Auditing** - every request, session and refused login becomes an event on RabbitMQ (Kafka is
  coming soon) or in a file: who ran what, from where, as which SQL Server login, and how it
  ended - in order, with heartbeats and gap reports so a consumer can tell that nothing went
  missing.

> **Passwords are never logged.** Not the DaffyTee password a user logs in with, not the SQL
> Server password it maps to, not RabbitMQ's: none of them appears in captured events, the SQL
> log, DaffyTee's own log, traces or transcripts. DaffyTee passwords are kept only as salted
> hashes; target passwords are stored encrypted and sent nowhere but to SQL Server.
>
> **Passwords written into SQL are masked too, by default.** `CREATE LOGIN` and `ALTER LOGIN ...
> WITH PASSWORD = '...'`, `CREATE CREDENTIAL ... SECRET = '...'`, master and certificate key
> passwords, `sp_addlinkedsrvlogin`, `ENCRYPTBYPASSPHRASE`, a password in a connection string passed
> to `OPENDATASOURCE`, parameters with names like `@password`, and the passwords given to DaffyTee's
> own [commands](#administer-in-t-sql) all reach events, the SQL log and `:watch` as `'***'`, and
> those requests carry no raw bytes. This also covers SQL built as a string
> (`EXEC('...')`, `sp_executesql`) and a password passed in a variable. The masking runs where events
> are written, never in the database conversation, so it does not slow sessions down. `--no-redact`
> turns it off.
>
> What cannot be recognized is free text: a password in a comment, or stored as ordinary data
> (`INSERT ... VALUES`), is captured as sent. `--trace` and `--transcript`, debugging aids that are
> off unless you ask for them, show the protocol exactly as sent and are not masked.

## What it is for

- **Legacy application investigation** - find out what an application nobody fully understands
  really sends: every statement and procedure call, with its actual parameter values.
- **Troubleshooting and unexpected SQL behavior** - see the exact SQL behind an error, a timeout,
  a wrong result or a surprise load, whether it came from the application, an ORM, SSMS or a
  reporting tool.
- **SQL performance analysis** - duration, time to first byte, rows and bytes for every request,
  tied to the user, application and host that sent it.
- **Application modernization and database migration** - capture production behavior before
  moving a workload: the real queries, parameters, volumes and transactions, as T-SQL you can run
  against the new platform.
- **Security investigations and access intervention** - see who did what, from where, including
  the logins that were refused. Cut off a user, or end a session, at once - without changing
  logins on SQL Server.
- **Compliance and audit** - an ordered record of database activity, kept outside the database it
  describes, with gap reports that show whether anything was lost.
- **An operational data platform** - feed SQL activity, as JSON events, into an operational data
  store, a SIEM or an analytics database.

## How it works

Each session is relayed, byte for byte, to a real SQL Server under a login mapped for that user -
apart from DaffyTee's own [`daffytee.*` commands](#administer-in-t-sql), which it answers itself.
Every request that passes through - the SQL, the procedure, its parameters, who sent it, how long
it took, how it ended - is copied asynchronously to RabbitMQ or a file. The name is Unix's `tee`:
one stream in, two out.

The copy never slows the database conversation down or puts it at risk: if the queue is slow or
gone, DaffyTee keeps relaying, holds what it can in a bounded buffer, and reports exactly what it
had to drop.

```
dotnet tool install -g DaffyTee      # install
dotnet tool update -g DaffyTee       # upgrade to the latest version
daffytee --version                   # which version is installed
```

## Use it

**1. Map a user.** A DaffyTee login, and the SQL Server login its sessions will use. The target
login is tried before anything is saved:

```
daffytee user add bob --target "Server=tcp:sql01,1433;User Id=svc_app;Encrypt=Mandatory;TrustServerCertificate=True"
```

It prompts for Bob's DaffyTee password, and for the target password the connection string leaves
out - secrets are typed, never put on a command line. The two passwords are separate: DaffyTee
never forwards the one Bob logs in with.

`TrustServerCertificate=True` keeps the connection to SQL Server encrypted, but accepts the
server's certificate without checking who issued it. Most SQL Servers present the self-signed
certificate they create at install, which fails that check - the most common first stumble, so the
examples include it. Once the server has a certificate this machine trusts, leave it out.

**2. Run it**, sending events to a file (or `--sink rabbit`):

```
daffytee --sink file --sql-log "sql-{date}.log"
```

This writes two files in the folder you start it from, one for machines and one for people:

- `daffytee-events-{date}.ndjson`, from `--sink file` - every event as a line of JSON, exactly
  what would go to RabbitMQ. `--sink-path` puts it somewhere else.
- `sql-{date}.log`, from `--sql-log` - the same requests as runnable T-SQL.

Either works alone: leave out `--sink file` for just the SQL log. `{date}` starts a new file each
day. Keep the quotes: unquoted, PowerShell drops everything from the `{` on.

**3. Connect** any SQL Server client to DaffyTee, as Bob:

```
Server=tcp:127.0.0.1,1433;Encrypt=Mandatory;TrustServerCertificate=True;User Id=bob;Password=...
```

SSMS, sqlcmd, ADO.NET, JDBC/DBeaver, ODBC all work. So do the ODBC driver and OLE DB provider
that ship with Windows (`Driver={SQL Server}`, `Provider=SQLOLEDB`), which speak the older TDS 7.1:
connect them with `Encrypt=no`, and they encrypt the whole session because DaffyTee requires it.
`SELECT SUSER_SNAME()` answers `svc_app` - the session really is on sql01, as the mapped login. A client that cannot be told
`TrustServerCertificate=True` - Power BI's SQL Server connector, for one - needs Windows to trust
DaffyTee's certificate first; see [below](#clients-that-check-the-certificate).

**4. Watch.** At DaffyTee's prompt, `:watch` streams each captured request as runnable T-SQL:

```
-- 2026-09-23 12:13:50.908  s10#1  bob -> svc_app@sql01:1433/Sales  app=OrdersApi  1.67 ms  ok
DECLARE @tvp1 dbo.IdList;
INSERT @tvp1 VALUES
    (1),
    (3);
EXEC dbo.GetCustomersByIds @Ids = @tvp1;
GO
```

## Clients that check the certificate

`TrustServerCertificate=True` tells a client to accept DaffyTee's self-signed certificate without
checking who issued it. Some clients have no such setting - Power BI's SQL Server connector is the
one most people meet first - and an application that checks certificates, as current drivers do by
default, has to be changed to stop. On Windows, DaffyTee can put its certificate among your trusted
roots; after that, those clients connect as they would to any SQL Server:

```
Server=tcp:127.0.0.1,1433;Encrypt=Mandatory;User Id=bob;Password=...
```

The first time you run `daffytee` in a terminal on Windows, its prompt offers to do this, before
your first command. Say no and it does not ask again, unless the certificate changes. Either way,
it can be done later:

| Command | What it does |
|---|---|
| `daffytee cert`, or `:cert` at the prompt | where the certificate is, the names it covers, when it expires, and whether Windows trusts it |
| `daffytee cert trust`, or `:cert trust` | add it to your trusted roots. Windows then shows its own security warning about installing the certificate: answer **Yes** |
| `daffytee cert untrust`, or `:cert untrust` | take every DaffyTee certificate back out; Windows asks to confirm each one. Do this before uninstalling DaffyTee, which leaves the store as it is |

`daffytee --trust-cert` and `--untrust-cert` do the same, as in DaffyDB.

What trusting does, and does not do:

- It adds only DaffyTee's certificate - the public half, never its private key - and only for your
  Windows account (Current User > Trusted Root Certification Authorities). No administrator
  rights, and other accounts on the PC are unaffected.
- Windows accepts DaffyTee under the names on its certificate: `localhost`, `127.0.0.1` and this
  machine's name. The certificate is not a certificate authority, so it cannot vouch for any other
  site or program.
- DaffyTee only ever adds the certificate it generated itself. A `--cert-file` certificate from
  your own certificate authority needs nothing from DaffyTee.
- If the certificate is replaced - `--regen-cert`, or when it expires after five years - trust the
  new one.

**Power BI Desktop:** Get Data > SQL Server, server `127.0.0.1` (`127.0.0.1,PORT` when DaffyTee is
not on 1433). When it asks for credentials, choose **Database** and give a DaffyTee login and its
password.

**Clients on other machines** (with `--bind`): trusting on DaffyTee's machine does nothing for
them. Copy `daffytee.cer` - `daffytee cert` shows where it is - to the client machine, and install
it there:

1. Right-click `daffytee.cer` and choose **Install Certificate**.
2. Choose **Current User**, then **Next**.
3. Choose **Place all certificates in the following store**. Do not leave it on "Automatically
   select", which does not put the certificate among the trusted roots.
4. Click **Browse**, select **Trusted Root Certification Authorities**, and click **OK**. Then
   click **Next** and **Finish**, and **Yes** to the security warning.

Then connect by DaffyTee's machine name - one of the names `daffytee cert` lists - rather than its
IP address. The same steps work on DaffyTee's own machine, if you would rather do it by hand.

On Linux and macOS DaffyTee leaves the system's trust store alone: use `TrustServerCertificate=True`,
or `Encrypt=Strict;ServerCertificate=/path/to/daffytee.cer`.

## What is captured

One event per request, in CloudEvents JSON: batches (the SQL text), procedure calls and
parameterised queries (`sp_executesql` with every parameter typed and valued - table-valued
parameters as rows), `SqlTransaction` begin/commit/rollback/save, and bulk loads (counted by
default; `--bulk full` for the rows). Each carries the DaffyTee login, the mapped target login and
its SPID, the database, the client host, application and address, the transaction it ran in,
timing, row counts, errors, output parameters and return status. Session start and end, refused
logins, and DaffyTee's own heartbeats and gap reports complete the stream.

Everything needed to rebuild a call is there, except passwords (see the top of this page);
`--raw` adds each request's exact bytes as well. The full [event reference](#event-reference) is at the end of this page.

## RabbitMQ

```
daffytee config rabbit amqp://daffytee@mq01:5672/audit      # prompts for the password; stored encrypted
daffytee --sink rabbit
```

Events go to a durable topic exchange, `daffytee`, routed by type: bind `request.*` for requests,
`session.#` for an access log, `#` for everything. Messages are persistent and publisher-confirmed.

If RabbitMQ goes away, sessions carry on untouched. Events queue in memory up to
`--queue-max-mb` (256 MB) or `--queue-max-events` (100,000); past that the newest are dropped,
counted, and reported to consumers as a `proxy.gap` event once the broker is back. The log says
when the broker went away, when the queue filled, and when it all recovered - once each, not on
every retry.

**Kafka is coming soon** as an alternative: a configuration choice, one broker or the other,
never both.

## The prompt

Run in a terminal, DaffyTee listens and gives you a `daffytee>` prompt at the same time. Commands
start with a colon, and `:help` lists them. Every command takes effect immediately: nothing
restarts, and connected clients are not disturbed. The prompt runs no SQL - that goes through a
client, as usual. While you type, DaffyTee's own log lines are held back so they never interrupt
you; `:log` shows them.

### See what is going on

**`:status`** is the health check on one screen: how long DaffyTee has been up; sessions; logins
that succeeded or were refused (and how many of those because the target refused or could not be
reached); what is being captured; where events go, and for RabbitMQ whether the broker is healthy
and when it last took a delivery; how full the in-memory queue is; and how many events were
captured, delivered and dropped, with the reason for any drops.

```
daffytee> :status

  up 0d 04:12:09, since 2026-09-23 09:14:02
  sessions : 12 active, 1318 accepted in total
  logins   : 1316 succeeded, 2 refused (0 because the target could not be reached or refused)
  tee      : on, sampling 100% of requests, bulk meta, passwords masked
  outputs  : rabbitmq amqp://daffytee@mq01:5672 vhost=audit exchange=daffytee; :watch / :recent
  sink     : healthy; last delivery 13:26:10
  queue    : 3 event(s), 0.0 MB of 256 MB / 100,000 (0% full)
  counts   : 184,221 captured, 186,870 delivered, 0 dropped
```

**`:sessions`** (or `:s`) lists every connected client: DaffyTee's session id, the DaffyTee login,
the target login and server it is relayed to, its SPID on that server, the current database, the
application name the client sent, where it connects from, requests so far, traffic in both
directions, and how long it has been idle. A session running a long query is listed while it runs,
and its `spid` is the number SQL Server's own tools know it by - `sp_who2`, `sys.dm_exec_requests`,
`KILL`.

```
daffytee> :sessions
  id  login  target                  spid  db     app        from             requests  traffic  idle
  --  -----  ----------------------  ----  -----  ---------  ---------------  --------  -------  ----
  17  bob    svc_app@sql01:1433      63    Sales  OrdersApi  127.0.0.1:51234  1204      812 KB   2s
  18  alice  svc_reports@sql01:1433  71    Sales  SQLCMD     127.0.0.1:54870  3         0 KB     4s
```

**`:kill ID`** disconnects one DaffyTee session, by its `id` from `:sessions` - not by SPID.
DaffyTee closes the session's two connections: the client's, and its own to SQL Server. SQL Server
sees its client go away, as if the application had crashed: it stops the request that was running,
rolls back any open transaction, and ends the session. The client sees a dropped connection.

```
daffytee> :sessions
  id  login  target                  spid  db     app        from             requests  traffic  idle
  --  -----  ----------------------  ----  -----  ---------  ---------------  --------  -------  ----
  17  bob    svc_app@sql01:1433      63    Sales  OrdersApi  127.0.0.1:51234  1204      812 KB   2s
  18  alice  svc_reports@sql01:1433  71    Sales  SQLCMD     127.0.0.1:54870  3         0 KB     4s

daffytee> :kill 18
  session 18 (alice) disconnected
```

> **`:kill` is not SQL Server's `KILL`.** DaffyTee sends no command to SQL Server; it only closes
> its own connection, so the mapped login needs no extra permission. The server-side session ends
> because its connection is gone. Anything that notices a lost connection only when it finishes - a
> call to a linked server, for instance - runs on until then, and rolling back a large transaction
> takes as long as it would after a `KILL`. To end a session on SQL Server directly, a DBA can pass
> the `spid` from `:sessions` to `KILL` on the server.

### Manage users

The same user commands as `daffytee user ...`, without stopping DaffyTee. Changes are saved to
the encrypted store at once and apply from the next login; sessions already connected carry on as
they are.

| Command | What it does |
|---|---|
| `:users` | every mapping: login, whether it is enabled, its target, when the target was last validated, when it last changed |
| `:user add NAME` | create a mapping. Prompts for NAME's DaffyTee password (twice), then the target: server, login, password, default database, encryption. Logs in to the target to prove the mapping works, and saves nothing if it does not |
| `:user add NAME --target "..."` | the same, with the target given as a connection string; only a password it leaves out is prompted for |
| `:user show NAME` | one mapping in full: target, encryption, and when it was last validated, against which SQL Server version. Never a password |
| `:user test NAME` | log in to NAME's target again, now - after a password rotation or a server move, say |
| `:user passwd NAME` | set a new DaffyTee password for NAME. The target login and its password are unchanged |
| `:user target NAME` | point NAME somewhere else - another server, database or target login - or enter the target's new password after a rotation. Validated before it is saved |
| `:user disable NAME`, `:user enable NAME` | refuse NAME's new logins, or allow them again, keeping the mapping |
| `:user admin NAME on`, `:user admin NAME off` | let NAME run DaffyTee's [admin commands in T-SQL](#administer-in-t-sql), or stop it |
| `:user remove NAME` | delete the mapping |

```
daffytee> :user add alice --target "Server=tcp:sql01,1433;User Id=svc_reports;Initial Catalog=Sales;TrustServerCertificate=True"
  new DaffyTee login 'alice'
  DaffyTee password for alice: ************
  again, to confirm: ************
  password for target login svc_reports: ********
  validating svc_reports@sql01:1433/Sales (Mandatory (TDS 7.4), certificate trusted without validation) ...
  ok: Microsoft SQL Server 16.0.4295, database 'Sales', Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256, 72 ms
  saved 'alice' -> svc_reports@sql01:1433/Sales

daffytee> :users
  login  enabled  admin  target                        validated         updated
  -----  -------  -----  ----------------------------  ----------------  ----------------
  alice  yes             svc_reports@sql01:1433/Sales  2026-09-23 14:25  2026-09-23 14:25
  bob    yes             svc_app@sql01:1433            2026-09-22 16:02  2026-09-22 16:02

daffytee> :user disable alice      # new logins as alice are refused from now on...
daffytee> :sessions                # ...find the session alice already has open...
  id  login  target                  spid  db     app     from             requests  traffic  idle
  --  -----  ----------------------  ----  -----  ------  ---------------  --------  -------  ----
  18  alice  svc_reports@sql01:1433  71    Sales  SQLCMD  127.0.0.1:54870  3         0 KB     4s

daffytee> :kill 18                 # ...and end it, by its id (not the spid)
  session 18 (alice) disconnected
```

### Choose what is captured

Each change is written to the log and sent to consumers as a `proxy.control` event, saying who
made it, so a gap in the stream that was somebody's decision can be told apart from one that was
not. Admins can make most of these changes [in T-SQL](#administer-in-t-sql) too; `:tee off` and
`:purge` are made only here.

| Command | What it does |
|---|---|
| `:tee` | show what is being captured |
| `:tee off`, `:tee on` | stop or resume capturing. Sessions are unaffected either way - the relay never depends on the tee |
| `:sample 10%` | capture a random one request in ten (`:sample 0.1` means the same) |
| `:sample 25% session` | capture a random quarter of connections - every request in them - rather than scattered requests; better for following one user's work from start to end |
| `:sample 100%` | back to capturing everything, the default |
| `:bulk meta` | bulk loads (`SqlBulkCopy`, bcp) recorded as rows, bytes and duration, without copying the rows. The default, and it costs no memory |
| `:bulk full` | the rows as well, up to `--max-event-kb` per event |
| `:bulk off` | no event for the bulk data at all (the `INSERT BULK` statement that starts a load is still captured) |
| `:raw on`, `:raw off` | add, or stop adding, each request's exact bytes (base64) to its event, for byte-exact replay |
| `:purge` | throw away everything waiting in the queue - after a long broker outage, say. Consumers receive a `proxy.gap` event with the reason `purged` |

### Watch it happen

| Command | What it does |
|---|---|
| `:watch` | stream captured requests as runnable T-SQL as they complete, until you press a key. Needs no sink: the quickest way to see what an application really sends |
| `:recent`, `:recent 50` | the last 10 (or 50) captured events, as T-SQL. The last 500 are kept |
| `:log`, `:log 100` | DaffyTee's own log: the last 20 (or 100) lines |
| `:log follow` | stream DaffyTee's log live, until you press a key |

### Stop

| Command | What it does |
|---|---|
| `:help` (or `:?`) | the command list |
| `:quit` (or `:q`, `:exit`) | disconnect every session, deliver what is still queued (for up to `--drain` seconds, 10 by default), and stop. Ctrl+C does the same |

With no terminal - output redirected, or `--no-repl` - DaffyTee runs without the prompt, and
`--log-file "daffytee-{date}.log"` keeps its operational log as JSON lines.

## Administer in T-SQL

Admins can do most of what the prompt does from SSMS, sqlcmd or their own code, from wherever they
are. They connect to DaffyTee as anyone does, and run:

```sql
EXEC daffytee.help
```

Anything in the `daffytee` schema is DaffyTee's own: DaffyTee answers it itself, and it never
reaches SQL Server. Everything else goes to the server as usual.

**The first admin** is made on DaffyTee's host; after that, admins can make others with
`daffytee.set_admin`:

```
daffytee user admin alice on        # or :user admin alice on, at the prompt
```

Until someone is made an admin, every admin command is refused, so an installation that never
makes one works exactly as before.

```sql
EXEC daffytee.sessions                  -- who is connected, relayed where, as which SPID
EXEC daffytee.kill_session 18           -- by its session id there, not the SPID
EXEC daffytee.disable_user 'bob'        -- bob's new logins are refused
EXEC daffytee.recent 20                 -- the last 20 captured events, with their SQL
EXEC daffytee.set_sample '10%'
```

For everyone:

| Command | What it does |
|---|---|
| `daffytee.help ['command']` | what you can run, with examples |
| `daffytee.status` | your own session: its id, its target and SPID there, how it is encrypted, whether you are an admin |

For admins, to see what is going on:

| Command | What it does |
|---|---|
| `daffytee.sessions` | every connected session, as `:sessions` lists them, with traffic and idle time |
| `daffytee.health` | uptime, sessions, logins, where events go, how full the queue is, what was dropped - `:status` as one row |
| `daffytee.settings` | how this DaffyTee is configured, and how each setting is changed |
| `daffytee.recent [@count]` | the last captured events (20, up to 500), with their SQL |
| `daffytee.log [@count]` | DaffyTee's own log (50 lines, up to 1,000) - also when it runs without a prompt |
| `daffytee.users` | every login: admin or not, enabled or not, its target, its last validation. Never a password |

For admins, to change things:

| Command | What it does |
|---|---|
| `daffytee.kill_session @session` | disconnect a session, as `:kill` does |
| `daffytee.set_sample @rate [, @by]` | as `:sample`: `'10%'`, `0.25` or `'100%'`, of `'request'`s or `'session'`s. Not 0 |
| `daffytee.set_bulk @mode` | as `:bulk`: `off`, `meta` or `full` |
| `daffytee.set_raw @raw` | as `:raw`: 1 or 0 |
| `daffytee.add_user @login, @password, @target` | create a login and its target. DaffyTee logs in to the target first, and saves nothing if that fails |
| `daffytee.set_user_password @login, @password` | a new DaffyTee password; the target is unchanged |
| `daffytee.set_target @login, @target` | a new target. Its password is kept if the new connection string has none and names the same login |
| `daffytee.set_target_password @login, @password` | the target's new password (or Entra client secret) after a rotation. Tried before it is saved |
| `daffytee.test_user @login` | log in to its target now, and say how it went |
| `daffytee.enable_user`, `daffytee.disable_user @login` | allow or refuse its new logins; sessions already open carry on |
| `daffytee.set_admin @login, @admin` | make it an admin (1), or not (0) |
| `daffytee.remove_user @login` | delete the login and its target |

**Kept on the host.** `:tee off` and `:purge` throw away the record DaffyTee keeps, so they are
done only at its prompt: a leaked admin password should not also be able to switch off the record
of what was done with it. For the same reason, sampling cannot be set to 0 from T-SQL. Whatever
needs a restart or the host itself - where events go, `--no-redact`, the certificate - is shown by
`daffytee.settings` and changed on the host.

**On the record.** Every admin command, and every refused attempt at one, becomes an ordinary
request event marked with `gatewayCommand`, however the tee and sampling are set, with its
passwords masked. Capture changes are `proxy.control` events that name the login that made them,
and DaffyTee's log has an `admin.command` line for each command. An admin cannot disable, demote or
remove their own login from T-SQL; another admin, or the host, can.

**How to call them:**

- **One command per batch.** `SELECT 1; EXEC daffytee.status` is refused with error 50201 rather
  than half run. In SSMS, select just that line, or put `GO` before and after it. `EXEC` and the
  quotes are optional: `daffytee.help sessions` works too. SSMS's IntelliSense may underline
  `daffytee.*` names it cannot find on the server; they run.
- **Arguments** go by position or by name (`@login = 'bob'`), as for any stored procedure.
- **From code**, a command is an ordinary stored procedure call:

  ```csharp
  using var cmd = new SqlCommand("daffytee.kill_session", connection) { CommandType = CommandType.StoredProcedure };
  cmd.Parameters.AddWithValue("@session", 18);
  cmd.ExecuteNonQuery();
  ```
- **In a transaction** a command works, but is not part of it: a change is made at once, and a
  `ROLLBACK` does not undo it.
- **Pooled connections** work as usual.
- **Not on MARS connections.** With `MultipleActiveResultSets=True` a command goes to SQL Server,
  which answers "Could not find stored procedure" (2812). Use a connection without it - SSMS's
  query windows are.
- **Not inside `sp_executesql`.** A parameterised `EXEC daffytee.status` is not looked into; call
  the command as a stored procedure instead.

**Passwords in T-SQL.** The connection to DaffyTee is encrypted, but what you type in SSMS can stay
behind in its query history and AutoRecover files, as with `CREATE LOGIN ... WITH PASSWORD`. To
keep a password out of any script, set it on DaffyTee's host - `daffytee user passwd NAME` and
`daffytee user target NAME` ask for it at a masked prompt - or, from your own workstation, pass it
as a parameter read from a masked prompt:

```powershell
$admin  = Get-Credential -Message "Your DaffyTee admin login"
$secret = Read-Host "New target password for bob" -AsSecureString

$cn = New-Object System.Data.SqlClient.SqlConnection(
    "Server=tcp:DEV01,1433;Encrypt=True;TrustServerCertificate=True;" +
    "User Id=$($admin.UserName);Password=$($admin.GetNetworkCredential().Password)")
$cn.add_InfoMessage({ param($s, $e) Write-Host $e.Message })
$cn.Open()
$cmd = $cn.CreateCommand()
$cmd.CommandText = "daffytee.set_target_password"
$cmd.CommandType = [System.Data.CommandType]::StoredProcedure
[void]$cmd.Parameters.AddWithValue("@login", "bob")
[void]$cmd.Parameters.AddWithValue("@password", [System.Net.NetworkCredential]::new("", $secret).Password)
[void]$cmd.ExecuteNonQuery()
$cn.Close()
```

**Errors** are ordinary SQL Server errors at severity 16, with messages that start `DaffyTee:`, so
applications can catch them by number:

| # | Meaning |
|---|---|
| 50201 | a `daffytee.*` command shared its batch with other SQL - run it on its own |
| 50202 | no such command, or the wrong arguments - the message shows how to call it |
| 50203 | an admin command from a login that is not an admin |
| 50204 | no such login or session, or a login that already exists |
| 50205 | refused by a rule - your own session or login, sampling 0% - the message says which |
| 50206 | the target could not be logged in to, so nothing was saved |

## Good to know

- **Targets:** SQL Server on-premises, in a VM, or in a container, and Azure SQL Database.
  Microsoft Fabric (Warehouse, SQL analytics endpoint, SQL database), Azure SQL Managed Instance
  and Azure Synapse SQL speak the same protocol - the same redirect and the same Entra ID
  application login - so they are expected to work, but have not been tested yet.
- **Azure SQL Database** works with a SQL login, including a contained database user - put its
  database in the mapping (`Initial Catalog=`) - or as an Entra ID application (below). Under
  Azure's *Redirect* connection policy the
  gateway sends each login on to the database's node (ports 11000-11999); DaffyTee follows that
  redirect itself, so clients still only ever connect to DaffyTee, and every event says which node
  the session ran on (`routedTo`). Azure's certificates are real ones: leave `TrustServerCertificate`
  out. A serverless database that has paused itself takes a while to resume on the first login, so
  give such a mapping `Connect Timeout=60`.

  ```
  daffytee user add bob --target "Server=tcp:myserver.database.windows.net,1433;User Id=app_user;Initial Catalog=Sales;Encrypt=Mandatory;Connect Timeout=60"
  ```
- **Entra ID application (service principal) as the target login**, for Azure SQL - the same
  keywords as SqlClient: `Authentication=ActiveDirectoryServicePrincipal`, and the application
  (client) id as `User Id`. DaffyTee prompts for the client secret, gets an access token from
  Entra ID and logs in with that; the secret never reaches SQL Server. Tokens are reused until
  shortly before they expire, so a burst of connections asks Entra ID once. DaffyTee sends the
  secret only to Microsoft's own Entra ID sign-in addresses and asks only for Azure SQL tokens,
  whatever the server says. It needs outbound HTTPS to `login.microsoftonline.com` /
  `login.windows.net`. The database user is the application's (`CREATE USER [app-name] FROM
  EXTERNAL PROVIDER`).

  ```
  daffytee user add bob --target "Server=tcp:myserver.database.windows.net,1433;Authentication=ActiveDirectoryServicePrincipal;User Id=<application id>;Initial Catalog=Sales;Encrypt=Mandatory;Connect Timeout=60"
  ```
- **Encryption is mandatory by default.** A self-signed certificate is generated on first run;
  `Encrypt=Optional` clients are upgraded to full TLS, and clients that cannot encrypt are
  refused. On Windows, `daffytee cert trust` lets clients that check the certificate accept it
  (see [above](#clients-that-check-the-certificate)). `--cert-file` presents a real certificate;
  `--allow-plaintext` is for development.
- **Listens on 127.0.0.1** until told otherwise: `--bind 0.0.0.0`.
- **Clients log in with a DaffyTee login and password.** Windows and Entra ID authentication from
  the client are not relayed. Toward the target, DaffyTee logs in with a SQL login, or to Azure SQL
  as an Entra ID application.
- **Connection pooling works** and is recommended; so do transactions, MARS, cancellation,
  bulk copy, table-valued parameters and `nvarchar(max)` parameters. One client connection is
  always one SQL Server connection.
- **Overhead** measured on one machine: about 30 µs per round trip and 4% on a million-row read;
  20,000 requests a second across 200 sessions, fully captured, in about 110 MB.
- User mappings are stored encrypted (AES-256-GCM, key protected by DPAPI on Windows, owner-only
  on Linux); DaffyTee login passwords are hashed, not stored.

## Not supported yet

- **Kafka** - coming soon, as an alternative to RabbitMQ (see above).
- **Running as a Windows service** - planned for a later release. For now, run DaffyTee in a
  terminal, where the prompt shows what it is doing.
- **Named instances by name** (`sql01\SALES`) - give the instance's port instead (`sql01,50123`).

This is an early release.

## Event reference

The contract for anything that consumes what DaffyTee publishes: governance, auditing, an
operational data store, threat analysis, or a developer reading the file sink. The examples were
captured from real sessions (the `proxy.gap` one is abridged).

### Transport

**RabbitMQ** (`--sink rabbit`):

| | |
|---|---|
| Exchange | `daffytee` (durable, **topic**), or `--rabbit-exchange` |
| Routing key | the event type: `request.rpc`, `session.start`, `proxy.gap`, ... |
| Body | the whole event, UTF-8 JSON (CloudEvents *structured* mode) |
| Properties | `content_type=application/cloudevents+json`, `message_id=` the event id, `type=` the event type, `timestamp`, `app_id=daffytee`, persistent |
| Headers | `daffytee-instance`, and on session-scoped events `daffytee-session`, `daffytee-login`, `daffytee-target`, `daffytee-database`, `daffytee-app` |

DaffyTee declares the exchange; **you declare and bind your own queues**. Typical bindings:

| Binding key | Receives |
|---|---|
| `#` | everything |
| `request.*` | every captured request |
| `request.rpc` | procedure calls and parameterised queries only |
| `session.#` | logins, logouts and refused logins - an access log |
| `proxy.#` | DaffyTee's own health: start, stop, heartbeat, gaps, setting changes |

Events are published *mandatory*. If no queue is bound for an event, RabbitMQ returns it rather
than dropping it silently; DaffyTee counts it as rejected and logs a warning once. `--rabbit-queue
NAME` declares a durable queue bound to `#`, which is convenient for testing.

**File** (`--sink file`): the same JSON documents, one per line (NDJSON), in
`daffytee-events-{date}.ndjson` or wherever `--sink-path` says.

### Delivery guarantees

- **At least once while DaffyTee is running.** Every event is confirmed by the broker before it
  counts as delivered. A batch interrupted by a broker failure is sent again whole, so an event
  can arrive twice. **De-duplicate on `id`.**
- **In order per session.** Events from one session are published in the order they happened;
  `partitionkey` (`s17`) names the session, and `data.seq` numbers its requests. Across
  sessions there is no ordering guarantee beyond `time`.
- **Never at the database's expense.** If the broker is slow or down, events wait in memory up to
  `--queue-max-mb` / `--queue-max-events`. Beyond that, *new* events are dropped (the oldest are
  kept, and delivered first when the broker returns) and the loss is reported as a
  `proxy.gap` event (below), placed in the stream where the loss happened.
- **Not across restarts.** The queue is in memory. A DaffyTee restart loses whatever was
  undelivered; `proxy.stop` (with its counts) and the next `proxy.start` make that visible.
- **Sampling is visible.** With `--sample`, `seq` still counts every request, so a jump in `seq`
  within a session means requests were sampled out, not lost. Session and `proxy.*` events are
  never sampled.

A consumer that needs to prove completeness checks three things: no unexpected `seq` jumps
within a session, no `proxy.gap` events, and a `proxy.heartbeat` at least every heartbeat
interval (60 s by default) while DaffyTee is up.

### Envelope

Every event is a [CloudEvents 1.0](https://cloudevents.io) JSON document:

| Field | |
|---|---|
| `specversion` | `"1.0"` |
| `id` | UUIDv7 - unique, and sortable by creation time |
| `source` | `daffytee://{instance}`, from `--instance` (default `host:port`) |
| `type` | the event type, identical to the routing key |
| `time` | UTC. For a request, when its first byte arrived at DaffyTee |
| `datacontenttype` | `"application/json"` |
| `partitionkey` | `s{session id}`, or `proxy` for events that belong to no session |
| `data` | the event itself; `data.v` is the schema version, currently `1` |

### Request events

One event per request, published when the response has finished (or the session ended first):

| Type | What the client sent |
|---|---|
| `request.batch` | T-SQL text (SQL_BATCH) |
| `request.rpc` | a procedure call: a stored procedure by name, or a system procedure such as `sp_executesql` (every parameterised query), `sp_prepexec`, `sp_execute` |
| `request.txn` | a transaction-manager request: how `SqlTransaction` begins, commits, rolls back and saves, without any `BEGIN TRAN` text |
| `request.bulk` | a bulk load (SqlBulkCopy, bcp); its `INSERT BULK` statement arrives separately as a `request.batch` |

```json
{
  "specversion": "1.0",
  "id": "01a0cf44-ba8f-79c9-bd2a-aae54afbfd2a",
  "source": "daffytee://DEV01:14330",
  "type": "request.batch",
  "time": "2026-09-23T17:16:23.0472968Z",
  "datacontenttype": "application/json",
  "partitionkey": "s1",
  "data": {
    "v": 1,
    "seq": 1,
    "durationMs": 3.087,
    "session": {
      "id": 1,
      "connectionId": "01a0cf44-ba1a-7ea9-962c-b7c5bcbd60cd",
      "login": "bob",
      "targetLogin": "svc_app",
      "target": "127.0.0.1:14333",
      "spid": 75,
      "database": "DaffyTeeTest",
      "host": "DEV01",
      "app": "TeeProbe",
      "client": "127.0.0.1:64037"
    },
    "request": {
      "kind": "batch",
      "txn": "0x0000000000000000",
      "bytes": 218,
      "packets": 1,
      "sql": "SELECT SUSER_SNAME(), HOST_NAME(), APP_NAME(), DB_NAME(), @@SPID, SERVERPROPERTY('ProductVersion')"
    },
    "response": {
      "status": "ok",
      "rowCount": 1,
      "done": "DONE",
      "doneStatus": "0x0010",
      "bytes": 206,
      "packets": 1,
      "firstByteMs": 3.087,
      "rowCounts": [1],
      "resultSets": [1]
    }
  }
}
```

#### data

| Field | |
|---|---|
| `seq` | the request's number within its session, counting every request |
| `durationMs` | from the request's first byte to the response's last, as seen by DaffyTee. Absent when the response never completed |
| `session` | who, where, and as whom - see below |
| `request` | what was sent |
| `response` | how it ended |

#### data.session

| Field | |
|---|---|
| `id` | DaffyTee's session number (resets when DaffyTee restarts) |
| `connectionId` | UUIDv7, unique across restarts |
| `login` | the **DaffyTee** login the client used |
| `targetLogin` | the SQL Server login it was mapped to |
| `target` | `host:port` of the SQL Server |
| `routedTo` | only when the SQL Server redirected the login (Azure SQL's Redirect policy, read-only routing): `host:port` of the server the session actually runs on |
| `spid` | the session id on that SQL Server (`@@SPID`) - joins to the server's own DMVs, audit and Extended Events |
| `database` | the current database, following `USE` and `ChangeDatabase` |
| `host`, `app` | the client's workstation and application name, as the client reported them |
| `client` | the client's IP address and port, as DaffyTee saw them |

#### data.request

| Field | Present | |
|---|---|---|
| `kind` | always | `batch`, `rpc`, `txn` or `bulk` |
| `txn` | batch, rpc, txn | the 8-byte transaction descriptor the request ran under; `0x0000000000000000` outside a transaction. Every statement in one transaction carries the same value |
| `gatewayCommand` | DaffyTee's own commands | `daffytee.sessions`, ...: the request was a [command](#administer-in-t-sql) DaffyTee answered itself, and never reached SQL Server; the response is DaffyTee's. Admin commands, and refused attempts, are captured whatever the tee and sampling are set to |
| `resetConnection` | when true | the first request on a pooled connection handed to a new user of the pool - a logical session boundary |
| `marsChannel` | on MARS connections | the logical channel (SMP session) that carried the request |
| `bytes`, `packets` | always | the request's size on the wire |
| `truncated` | when true | the request exceeded `--max-event-kb`; `sql`, parameters and `raw` stop short, `bytes` is the full size |
| `redacted` | when true | a password or other secret in `sql`, a parameter or an output parameter was replaced with `***`; such a request carries no `raw` |
| `decodeProblem` | when set | part of the request could not be decoded; the undecoded part is kept (see `calls[].undecoded`) |
| `sql` | batch | the T-SQL text, exactly as sent - apart from masked secrets (`redacted`) |
| `calls` | rpc | one entry per procedure call (a message can batch several) |
| `transaction` | txn | `op` (`begin`, `commit`, `rollback`, `save`, `promote`, `propagate`), `name`, `isolation`, `beginsNew` |
| `bulk` | bulk | `table`, from the `INSERT BULK` that announced it |
| `raw` | with `--raw`, or bulk with `--bulk full`; not when `redacted` | the request payload, base64, byte for byte |

#### data.request.calls[]

```json
{
  "proc": "sp_executesql",
  "procId": 10,
  "sql": "SELECT Name FROM dbo.Customers WHERE Id = @id",
  "paramDecl": "@id int",
  "params": [
    { "name": "@id", "type": "int", "tds": "0x26", "dir": "in", "value": 2 }
  ]
}
```

| Field | |
|---|---|
| `proc` | the procedure: `dbo.GetCustomer`, or a system procedure such as `sp_executesql` |
| `procId` | set when the client named a system procedure by number (10 = `sp_executesql`, 11 `sp_prepare`, 12 `sp_execute`, 13 `sp_prepexec`, 15 `sp_unprepare`, 1-9 the cursor procedures) |
| `sql` | the statement, for the procedures that carry one (`sp_executesql`, `sp_prepexec`, `sp_prepare`, `sp_cursoropen`...). For `sp_execute` it is the statement that handle was prepared with, when DaffyTee saw it being prepared |
| `paramDecl` | the parameter declarations that came with the statement |
| `handle` | the prepared-statement handle an `sp_execute` / `sp_unprepare` refers to |
| `params[]` | the parameter values - for `sp_executesql` and friends only the real parameters, not the statement and declaration arguments |
| `undecoded`, `undecodedBytes` | when decoding stopped part-way: why, and the rest of the call as base64 |

Each parameter: `name` (empty for positional), `type` (as declared, e.g. `nvarchar(50)`,
`decimal(18,2)`), `tds` (the wire type byte), `dir` (`in`, or `out` for OUTPUT - whose value
is what was sent *in*; the value that came back is in `response.output`), `default` (true when
the client asked for the parameter's default), `encrypted` (Always Encrypted), and `value`.

#### data.response

| Field | |
|---|---|
| `status` | `ok`; `error` (the server reported an error); `cancelled` (the client sent an attention - `SqlCommand.Cancel()` or a command timeout); `incomplete` (the session ended before the response did) |
| `rowCount` | rows affected or returned by the last statement, when the server reported a count |
| `done`, `doneStatus` | the final DONE token and its status bits, for anyone who wants the raw truth |
| `bytes`, `packets` | the response's size |
| `firstByteMs` | from the end of the request to the first byte of the response - roughly the server's think time |

When the whole response was no larger than `--response-kb` (64 KB by default), it was decoded too,
and these appear when they have something to say:

| Field | |
|---|---|
| `errors[]` | every error the server raised: `number`, `severity`, `state`, `message`, `procedure`, `line` |
| `messages[]` | informational messages: `PRINT` output, `RAISERROR` below severity 11, "Changed database context" (first 20) |
| `rowCounts[]` | every statement's count, in order - `[1, 1, 0, 1]` for four statements |
| `resultSets[]` | rows in each result set returned, in order |
| `returnStatus` | a stored procedure's `RETURN` value |
| `output` | OUTPUT parameter values as they came back: `{"@Name": "Grace Hopper"}` |
| `database` | the database the session moved to |
| `transactions[]` | transaction boundaries the server reported: `begin 0x0000004B00000001`, `commit`, `rollback` |
| `detailProblem` | why the response could not be fully decoded: for example Always Encrypted metadata, or the rows of a server cursor's fetch (`sp_cursorfetch`), whose column metadata came with the cursor's open in an earlier response |

Larger responses - big result sets - are not buffered; they still get `status`, `rowCount`, sizes
and timing.

### Session events

`session.start` - a client logged in and its target session is open:

```json
{
  "type": "session.start",
  "partitionkey": "s1",
  "data": {
    "v": 1,
    "session": { "id": 1, "login": "bob", "targetLogin": "svc_app", "target": "127.0.0.1:14333", "spid": 75, "database": "DaffyTeeTest", "host": "DEV01", "app": "TeeProbe", "client": "127.0.0.1:64037", "connectionId": "01a0cf44-ba1a-7ea9-962c-b7c5bcbd60cd" },
    "clientSecurity": "Mandatory (TDS 7.4), Tls12",
    "targetSecurity": "Tls12, TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256",
    "serverVersion": "Microsoft SQL Server 16.0.4295",
    "mars": false
  }
}
```

`session.end` - the session closed; `durationMs`, `requests`, `bytesFromClient`,
`bytesFromTarget`, and `reason` (`client disconnected`, `target disconnected`, `killed from the
console`, `killed by 'alice' (session 12) with daffytee.kill_session`, `DaffyTee is shutting down`).

`session.login_failed` - a login was refused. The client was told only
"Login failed for user 'x'" (error 18456) - or, when the SQL Server itself turned down what the
client asked for, the server's own error under its own number, without the mapped login's name:
4060 for a database it cannot open, 40613 for an Azure SQL database that is resuming. Client retry
logic decides by that number. This event says why:

```json
{
  "type": "session.login_failed",
  "partitionkey": "proxy",
  "data": {
    "v": 1,
    "login": "bob",
    "reason": "bad_password",
    "client": "127.0.0.1:64043",
    "host": "DEV01",
    "app": "TeeProbe",
    "database": "",
    "clientSecurity": "Mandatory (TDS 7.4), Tls12"
  }
}
```

| `reason` | |
|---|---|
| `unknown_user` | no such DaffyTee login |
| `bad_password` | wrong DaffyTee password |
| `empty_password` | none given - never accepted |
| `disabled` | the login exists but is disabled |
| `integrated_auth`, `federated_auth` | Windows or Entra ID authentication, which DaffyTee does not relay |
| `password_change` | the client tried to change a password through DaffyTee |
| `target_rejected` | DaffyTee accepted the login, the SQL Server refused the mapped one (wrong target password, database unavailable, login disabled) |
| `target_unreachable`, `target_security`, `target_protocol`, `target_routed` | the SQL Server could not be reached, TLS with it failed, it spoke nonsense, or it redirected the login a second time (one redirect is followed) |
| `target_token` | no Entra ID token for the mapping's application: Entra ID refused it (wrong or expired client secret) or could not be reached |
| `bad_mapping`, `mars_refused` | configuration problems |

### DaffyTee's own events

| Type | When | data |
|---|---|---|
| `proxy.start` | DaffyTee started | `teeEnabled`, `settings`, `redactSecrets` (false with `--no-redact`), `sink`, `version` |
| `proxy.stop` | DaffyTee is stopping | `captured`, `published`, `dropped` |
| `proxy.heartbeat` | every `--heartbeat` seconds (60) | `teeEnabled`, `sessions`, `captured`, `published`, `dropped`, `queueEvents`, `queueBytes`, `sinkState`, `uptimeSeconds` |
| `proxy.control` | someone changed the capture settings, at the prompt or in T-SQL | `change` (e.g. `tee off`, `sampling 10% of requests`), `by` (`console`, or the DaffyTee login that ran the command), `session` (the session it ran in, for T-SQL), `teeEnabled`, `settings` |
| `proxy.gap` | events were lost | see below |

#### proxy.gap

```json
{
  "type": "proxy.gap",
  "partitionkey": "proxy",
  "data": {
    "v": 1,
    "dropped": 39644,
    "droppedBytes": 1188120,
    "from": "2026-09-23T17:24:42.0123456+00:00",
    "to": "2026-09-23T17:25:00.456789+00:00",
    "reasons": { "queue_full": 39644 }
  }
}
```

`dropped` events (`droppedBytes` of request payload) were lost between `from` and `to`, for these
reasons: `queue_full` (the sink was down or slow for longer than the queue could hold),
`purged` (someone ran `:purge` at the prompt), `sink_rejected` (the broker refused them, or no
queue was bound), `shutdown` (still undelivered when DaffyTee stopped). A long outage can produce
several gap events in a row - add them up.

### Values

| SQL type | JSON |
|---|---|
| `int`, `bigint`, `smallint`, `tinyint` | number |
| `decimal`, `numeric`, `money`, `smallmoney` | number, exact (a `decimal(38,x)` beyond .NET's range is a string of digits) |
| `float`, `real` | number |
| `bit` | `true` / `false` |
| `char`, `varchar`, `nchar`, `nvarchar`, `text`, `ntext`, `xml` | string (varchar decoded in its collation's code page) |
| `binary`, `varbinary`, `image`, `timestamp` | base64 string |
| `date` | `"2026-09-23"` |
| `time` | `"13:14:15.1234567"` |
| `datetime`, `datetime2`, `smalldatetime` | `"2026-09-23T13:14:15.123"` (no zone: SQL Server stores none). `datetime` to the millisecond, as SQL Server shows it |
| `datetimeoffset` | `"2026-09-23T13:14:15.1234567+02:00"` |
| `uniqueidentifier` | `"6f9619ff-8b86-d011-b42d-00c04fc964ff"` |
| `sql_variant` | whatever its underlying value is |
| table-valued | `{"tableType": "dbo.IdList", "columns": [{"name": ..., "type": ...}], "rows": [[1], [3], [4]]}` |
| Always Encrypted | `{"ciphertext": "base64..."}` - DaffyTee never has the key |
| anything else (CLR types, `json`/`vector` on SQL Server 2025) | `{"undecoded": "why", "bytes": "base64..."}` |
| NULL | `null` |

### Rebuilding a call

Everything needed to re-issue a request, except masked passwords, is in the event:

- **Batch** - run `request.sql` in `session.database`.
- **sp_executesql** (and `sp_prepexec` / `sp_prepare`, which are equivalent) -
  `EXEC sp_executesql @stmt = calls[].sql, @params = calls[].paramDecl, <each param> = <value>`.
- **Stored procedure** - `EXEC calls[].proc <name> = <value>, ...`, with `OUTPUT` for `dir: "out"`.
- **Table-valued parameter** - declare a variable of `tableType`, insert `rows`, pass it.
- **Transaction** - `BEGIN TRANSACTION` / `COMMIT` / `ROLLBACK` / `SAVE TRANSACTION name`.
- **Byte-exact** - run DaffyTee with `--raw` and every request carries its exact TDS payload in
  `request.raw` - except a request with a masked password, whose bytes would still hold it.

The `--sql-log` file and the `:watch` / `:recent` console commands do exactly this, rendering each
event as a runnable T-SQL block (with DECLAREs for output and table-valued parameters).

Note that DaffyTee captures what the client sent. SQL that runs *inside* a stored procedure, or a
trigger, is the server's business and does not appear.

### Versioning

`data.v` is the schema version. Fields may be **added** within a version - consumers should
ignore fields they do not know. Renaming, removing or changing the meaning of a field increments
`v`.

## Protocol and trademarks

TDS began at Sybase in the 1980s and has many independent implementations - FreeTDS, jTDS,
Tiberius, and Babelfish for PostgreSQL among them. DaffyTee is one more, built from Microsoft's
published [MS-TDS] specification; it contains no Microsoft code and is not affiliated with,
endorsed by, or sponsored by Microsoft Corporation. Microsoft, SQL Server, Azure, Entra and Power BI
are trademarks of Microsoft Corporation. RabbitMQ is a trademark of Broadcom Inc. Apache Kafka is a trademark of the Apache
Software Foundation.

## License

DaffyTee is licensed under the **MIT License**, from version 0.3.2: free to use, copy, modify and
redistribute, in production or not, as long as the copyright and license notice stay with it.
Versions up to 0.3.1 were published under the Business Source License 1.1. The full text is in
`LICENSE.txt` in the package.

Copyright (c) 2026 Scott Traube and TroBeeOne LLC. Bundled dependencies keep their own licenses, listed in
`THIRD-PARTY-NOTICES.txt`: RabbitMQ.Client (Apache-2.0), System.Security.Cryptography.ProtectedData
and System.Threading.RateLimiting (MIT).

DaffyTee sends no data or telemetry to TroBeeOne. The events it captures hold the SQL, parameter
values and identities that pass through it, which may include personal or confidential
information; securing the host it runs on, and the brokers and files it writes to, is up to you.

## Contact

TroBeeOne LLC: [trobee.one](https://trobee.one) ·
[LinkedIn](https://www.linkedin.com/company/trobee-one-llc) ·
source on [GitHub](https://github.com/TroBeeOne/DaffyTee)
