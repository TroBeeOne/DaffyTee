# DaffyTee

<img src="icon.png" alt="DaffyTee icon" width="128" align="right">

[![NuGet](https://img.shields.io/nuget/v/DaffyTee.svg?label=NuGet)](https://www.nuget.org/packages/DaffyTee)
Install it from NuGet: [nuget.org/packages/DaffyTee](https://www.nuget.org/packages/DaffyTee)
(`dotnet tool install -g DaffyTee`).

A database gateway that lets existing applications reach SQL Server and Azure SQL through an
identity you control, and records everything they do - without changing the application or the
database. The name is Unix's `tee`: one stream in, two out - one to the database, one to whoever
wants to know what was said.

Clients connect to DaffyTee as if it were SQL Server. It authenticates them with its own logins,
logs in to the target independently as the identity mapped to each one - a SQL login, or an Entra
ID application for Azure SQL - follows Azure SQL's redirects itself, and relays each session byte
for byte. Every request - SQL, procedure, parameters, user, timing, outcome - is copied
asynchronously to RabbitMQ (Kafka coming soon) or a file. The copy never slows the database
conversation or puts it at risk.

**Passwords are never logged** - not DaffyTee's, not the target's, not RabbitMQ's - in events,
the SQL log, the operational log, traces or transcripts. Passwords written into SQL
(`CREATE LOGIN ... WITH PASSWORD = '...'`, credentials, key passwords, `@password` parameters) are
masked as `***` in events and the SQL log by default; `--no-redact` keeps them. Comments and
ordinary data are not recognized, and `--trace` / `--transcript` show SQL exactly as sent.

It shares its TDS front end with [DaffyDB](https://www.nuget.org/packages/DaffyDB), which answers
SQL Server clients from DuckDB; DaffyTee answers them from SQL Server itself.

- [Quick start](#quick-start) · [How it works](#how-it-works) · [Users and security](#users-and-security)
- [Administering in T-SQL](#administering-in-t-sql) · [Capture](#capture) · [RabbitMQ](#rabbitmq) · [The prompt](#the-prompt) · [Running it for months](#running-it-for-months)
- [Design notes](#design-notes) · [Testing](#testing) · [Limits](#limits) · [Layout](#layout)

The published event schema is the [event reference](NUGET.md#event-reference) on the package page.

## Quick start

```
dotnet tool install -g DaffyTee     # dotnet tool update -g DaffyTee to upgrade

daffytee user add bob --target "Server=tcp:sql01,1433;User Id=svc_app;Encrypt=Mandatory;TrustServerCertificate=True"
daffytee --sink file --sql-log "sql-{date}.log"
```

Keep the quotes around any file name with `{date}` in it: unquoted, PowerShell drops
everything from the `{` on, and the log is written to a file called `sql-`.

Then point any SQL Server client at `tcp:127.0.0.1,1433` as `bob`, with
`Encrypt=Mandatory;TrustServerCertificate=True`. At DaffyTee's prompt, `:watch` shows every
request as it happens.

From source: `dotnet run --project src/DaffyTee -- ...`.

## How it works

```
client ──TLS──► DaffyTee ──TLS──► SQL Server
                   │
                   │  both directions observed: headers tracked, requests copied (a memcpy)
                   ▼
            bounded queue ──► one worker: decode, JSON ──► RabbitMQ | NDJSON file
                                                     └──► :watch, :recent, --sql-log
```

**Login.** DaffyTee answers the client's PRELOGIN itself, completes TLS, and reads LOGIN7. It
checks the login against its own user store, then logs in to the mapped SQL Server as a client:
its own PRELOGIN and TLS, and the client's LOGIN7 with four fields replaced - user name, password,
server name, and (if the client named none) the database. Everything else the client said about
itself goes through as sent: TDS version, packet size, option flags, host, application, language,
and the FeatureExt block that negotiates session recovery, UTF-8, Always Encrypted and the rest.

The target's login response is relayed to the client **unchanged**. Its LOGINACK, real version,
packet size, collation and feature acknowledgements are therefore what both ends now believe, so
from that moment every byte can pass through untouched in both directions - including MARS
framing, attention (cancel) packets and pooled-connection resets. Nothing is parsed, reframed or
re-encoded on the way through. (`SqlClient`'s native SNI does rely on one thing a real server does:
one TLS record per packet. DaffyTee writes that way too - without it SNI.dll fails fast on large
results.)

**Commands.** The one exception to "untouched" is DaffyTee's own `daffytee.*` commands, which it
answers itself (`Commands/`, from DaffyDuo). On a session without MARS the client's side is read a
packet at a time - from the same large socket reads as before, so it costs no extra reads - and the
first packet of each request gets one vectorised search for `daffytee`: an RPC's procedure name, or
a batch's text. Only a request that mentions it is held back, read whole (up to 64 KB) and
tokenised; if it is not a command after all it goes on unchanged. Measured against 0.2.0, the
difference is lost in the noise. A command's answer is framed like a server's, at the negotiated
packet size and the session's SPID, and shown to the observers as if the target had sent it, so the
tee pairs it like any other request. Three things had to be right:

- **A pooled connection's reset** can arrive on a command, which the target never sees. DaffyTee
  sends it on a request of its own - a comment with the reset flag - reads the target's answer
  itself instead of relaying it, and puts the target's ENVCHANGE tokens in front of its reply.
- **Always Encrypted.** Microsoft.Data.SqlClient asks for column encryption on every connection and
  SQL Server grants it; the client then expects a key table in every COLMETADATA, DaffyTee's own
  included (an empty one). Without it, no SqlClient application could read a command's answer.
- **Not under MARS.** There the client's packets travel in SMP frames whose sequence numbers the
  target keeps; an answer could not be slipped in without renumbering every later frame. MARS
  sessions keep the plain byte pump, and their commands reach the target, which answers "Could not
  find stored procedure". Clients older than TDS 7.3 are left alone too.

**Tee.** Each direction is observed *before* it is forwarded, so what DaffyTee sees follows cause
and effect: a request before its answer, an answer before the next request. The observer follows
packet boundaries without copying, copies request payloads that are being captured into pooled
buffers, and keeps the last 13 bytes of each response (its final DONE token) plus the whole
response when it is small. A finished request/response pair is handed to a queue; one worker
decodes it into an event and publishes it. The relay never waits on that queue.

## Users and security

A mapping is one entity: a DaffyTee login, its password, and a target connection string.

```
daffytee user add NAME [--target "CONNECTION STRING"]
daffytee user list | show NAME | test NAME | passwd NAME | target NAME | enable NAME | disable NAME | remove NAME
daffytee user admin NAME on|off                 may run the admin commands in T-SQL
```

- **The target login is validated before anything is saved**, with the same connector the relay
  uses - "validated" means the relay will work.
- **The two passwords are separate.** Bob's DaffyTee password is hashed (PBKDF2-SHA256, 210,000
  iterations) and never forwarded. The target password is encrypted, because it has to be sent to
  SQL Server. Bob might map to a shared service account, or to his own SQL login with a different
  password.
- **The whole store is encrypted** - `users.dat`, AES-256-GCM - so not even the list of logins is
  readable on disk. The key is `store.key`, protected by DPAPI on Windows (this account by
  default; `--machine-scope` for a service running as another account) and by owner-only
  permissions (0600) elsewhere. `%LOCALAPPDATA%\DaffyTee` or `~/.local/share/DaffyTee`, or
  `--config-dir` / `DAFFYTEE_HOME`.
- **Secrets are typed, not passed as arguments.** Prompts are masked at a console; when stdin is
  redirected each prompt reads a line, which is how scripts drive them.
- A running DaffyTee picks up changes made from another terminal on the next login.
- Refused logins are answered with SQL Server's own error 18456, naming only the login the client
  used. The reason - and the target login involved - go to DaffyTee's log and to a
  `session.login_failed` event, never to the client. The exception is the target refusing what
  the client asked for (4060: a database it cannot open; 40613: an Azure SQL database resuming),
  which is passed on under its own number, the target login's name taken out, because client
  retry logic decides by number.

**Encryption is mandatory by default.** A self-signed certificate is generated on first run.
`Encrypt=Mandatory` (TDS 7.4) and `Encrypt=Strict` (TDS 8.0) share the port. A client asking for
`Encrypt=Optional` is answered with "encryption required" and so encrypts the whole session - as
do the TDS 7.1 ODBC driver and OLE DB provider that ship with Windows, given `Encrypt=no` (with
`Encrypt=yes` they check the certificate and cannot be told to trust a self-signed one); clients
that cannot encrypt are refused. `--allow-plaintext` relaxes that for development;
`--cert-file` presents a real certificate. Toward the target, each mapping chooses `Encrypt=
Mandatory` (default), `Strict`, or `Optional`, with `TrustServerCertificate` and
`HostNameInCertificate` as in SqlClient.

**Trusting the certificate (Windows).** Clients that cannot be told `TrustServerCertificate=True`,
such as Power BI's SQL Server connector, need Windows to trust the generated certificate. `daffytee
cert trust` (or `:cert trust`, or `--trust-cert` as in DaffyDB) adds its public half to the current
user's Trusted Root store, and Windows asks the user to confirm; `cert untrust` takes every DaffyTee
certificate back out, and `daffytee cert` shows the certificate and its state. The first
interactive run on Windows asks once, at the prompt, after the listener is up; a no is remembered
beside the certificate (`daffytee.trust-declined`, keyed by thumbprint) until the certificate
changes. Only a self-signed certificate that is not a certificate authority, cannot sign
certificates, and is for server authentication is ever added - the generated one qualifies; a
`--cert-file` certificate is never added. Nothing is asked under `--no-repl` or with input
redirected, so scripts never wait. (`Tds/CertificateTrust.cs`, from DaffyDB.)

DaffyTee listens on **127.0.0.1** unless `--bind` says otherwise.

## Administering in T-SQL

Admins - logins with the admin flag, the first one set on the host with `daffytee user admin NAME
on` - can run `EXEC daffytee.help` from SSMS or code and do most of what the prompt does: see
sessions, health, settings, recent events and the log; kill sessions; change sampling, bulk and raw
capture; and manage logins and their targets. [NUGET.md](NUGET.md#administer-in-t-sql) lists every
command. The pattern - one catalog, a tokenizer that refuses a command sharing its batch, arguments
by position or by name, help generated from the catalog - is DaffyDuo's; the schema is `daffytee`,
not DaffyDuo's `daffy`, so DaffyTee in front of DaffyDuo passes DaffyDuo's commands through, and the
errors are 502xx where DaffyDuo's are 501xx.

- **One implementation.** The commands work on what the prompt works on: `SessionRegistry.Kill`,
  `CapturePipeline.Control` and `Health`, and `UserAdmin` (shared with `daffytee user ...`). A
  session killed in T-SQL ends exactly as one killed with `:kill`, and a capture change is logged
  and published the same way, naming the login instead of the console.
- **Kept on the host:** `:tee off`, `:purge` and sampling at 0%, which throw away the audit record,
  and everything that needs a restart or the host itself.
- **On the record:** admin commands, and refused attempts, are captured whatever the tee and
  sampling say (`request.gatewayCommand`), with their passwords masked by `SecretRedactor` like any
  other; `admin.command` and `admin.refused` go to the log.
- **No self-lockout from T-SQL:** an admin cannot disable, demote or remove their own login there.

## Capture

Off unless a sink or `--sql-log` is configured (or `--tee on`); switchable at the prompt.

| Option | |
|---|---|
| `--sink none\|file\|rabbit` | where events go - one sink at a time |
| `--sink-path FILE` | the NDJSON file (`daffytee-events-{date}.ndjson`); `{date}` rolls daily |
| `--sql-log FILE` | every request as runnable T-SQL - a developer's log |
| `--sample RATE`, `--sample-by request\|session` | capture a fraction (default all). Session and `proxy.*` events are never sampled |
| `--bulk off\|meta\|full` | bulk loads: nothing, counts only (default - no memory cost), or the rows too |
| `--raw` | each request's exact bytes, base64, for byte-exact replay |
| `--max-event-kb N` | largest request copied into one event (1024); larger ones are marked truncated |
| `--response-kb N` | responses up to N KB (64) are decoded for errors, counts, output parameters |
| `--queue-max-mb N`, `--queue-max-events N` | the memory the queue may use (256 MB / 100,000) |
| `--heartbeat SECONDS` | `proxy.heartbeat` interval (60) |

Requests are decoded fully: T-SQL text; procedure calls with every parameter typed and valued
(MAX types, table-valued parameters as rows, `sql_variant`, `xml`; Always Encrypted values as
ciphertext); `sp_prepexec`/`sp_execute` resolved back to their statement; `SqlTransaction`
begin/commit/rollback/save; the transaction each request ran in. MARS connections are
demultiplexed channel by channel. See the [event reference](NUGET.md#event-reference).

## RabbitMQ

```
daffytee config rabbit amqp://user@host:5672/vhost      # prompts for the password, tests, stores encrypted
daffytee --sink rabbit [--rabbit-exchange daffytee] [--rabbit-queue NAME]
```

The URI can also come from `--rabbit` or `DAFFYTEE_RABBIT_URI`. Events are published persistent,
mandatory and publisher-confirmed to a durable topic exchange, routed by event type.

**When RabbitMQ is unavailable** the database path is unaffected - measured: a 24-second broker
outage under 2,000 requests/second produced zero client errors and no change in latency. Events
wait in memory, retried with backoff (up to every 10 s). When the queue is full the newest are
dropped - the oldest are kept and delivered first - and the loss is published as a `proxy.gap`
event where it happened. The log reports the outage, the backlog passing half full, dropping
starting, and recovery, once each, with a reminder every five minutes during a long outage.
Consumers can tell a DaffyTee that has stopped from a quiet one by missing heartbeats.

**Kafka is coming soon** as an alternative sink (one or the other, never both): the sink
interface is already broker-neutral.

## The prompt

Runs alongside the listener when there is a console (`--no-repl` to switch off; automatic when
output is redirected). Clients stay connected while you use it. [NUGET.md](NUGET.md#the-prompt)
describes every command with examples.

```
:status                          uptime, sessions, logins, the tee, the queue, the sink
:sessions (:s)                   who is connected, where to, and how much
:kill ID                         disconnect a session by its id in :sessions (not the SPID; no KILL is sent)
:users                           every mapping
:user add NAME [--target "..."]  create a mapping; the target is validated before it is saved
:user show|test|passwd|target|enable|disable|remove NAME
:user admin NAME on|off          may run the admin commands in T-SQL (EXEC daffytee.help)
:tee [on|off]                    capture on or off; sessions are unaffected
:sample RATE [request|session]   capture a fraction: :sample 10%, :sample 25% session
:bulk off|meta|full              bulk loads: nothing, counts (default), or the rows too
:raw on|off                      each request's exact bytes in its event
:watch                           captured requests as SQL, live, until a key
:recent [n]                      the last n captured requests (default 10)
:purge                           empty the queue (reported as a gap)
:log [n|follow]                  DaffyTee's own log
:cert [trust|untrust]            the certificate clients see; add it to Windows' trusted roots, or take it out
:help, :quit                     the list; disconnect everyone, drain the queue, stop
```

## Running it for months

- **In a terminal, for now.** Running as a Windows service is planned for a later release - a
  service is harder to debug and test, so the first releases keep DaffyTee where its prompt can
  be seen. `--no-repl` (automatic when output is redirected) runs it without the prompt;
  `--log-file "daffytee-{date}.log"` keeps the operational log.
- **Operational log** (`--log-file`): JSON lines - `{"ts","level","event","session","msg"}` - with
  stable event codes (`session.open`, `session.login_refused`, `session.killed`, `admin.command`,
  `admin.refused`, `sink.unavailable`, `sink.recovered`, `queue.backlog`, `queue.dropping`, ...) to
  alert on. It rolls daily with
  `{date}` in the name; delete old files with the tool you already use for that.
- **Health from outside**: `proxy.heartbeat` events, or the log.
- TCP keepalive is on for both halves of every session, so dead peers are noticed.
- A restart loses whatever events were still queued; `proxy.stop`/`proxy.start` make it visible.

## Design notes

Decisions worth knowing, and why.

**A byte relay, not ADO.** DaffyTee never opens a `SqlConnection`. Rewriting LOGIN7 and relaying the
login response verbatim lets both ends agree on everything, so the rest of the session is copied,
not translated - the client talks to SQL Server with DaffyTee out of the way, except when it talks
to DaffyTee itself with a `daffytee.*` command. It is also why
DaffyTee carries no SqlClient at all.

**Connection pooling** works and is recommended. DaffyDB had to switch it off because DuckDB could
not honour a pooled connection's reset; here the reset flag goes to the real server, which does
exactly what it always does. One client connection is always one SQL Server connection: DaffyTee
does not pool or multiplex upstream connections itself, because session state, temp tables,
transactions and per-user credentials make that unsafe, and client-side pools already amortise
logins. Events carry `resetConnection` so a consumer can see a pooled connection change hands.

**Transactions** need nothing special for the same reason: each session is its own relay.
`BEGIN TRAN`, `SqlTransaction`, savepoints, isolation levels all reach the server as sent. When a
client disconnects, its target session is closed and the server rolls back what was open;
DaffyTee never tries to be clever about recovery. Every captured request carries the transaction
descriptor it ran under, so consumers can group statements by transaction. Events are published
when a request completes, not when its transaction commits - a rolled-back transaction is still
in the stream, because the point is what came through.

**Events are decoded, not raw frames.** Consumers - governance, ODS, threat analysis - should not
each need a TDS decoder. `--raw` is there for byte-exact replay.

**Bounded by bytes and events, drop newest, report gaps.** An audit stream that silently loses
data is worse than one that says "lost 39,644 events between 17:24:42 and 17:25:00".

**Logging.** Two streams with two audiences: the audit events (CloudEvents JSON, versioned schema)
and DaffyTee's own operational log (JSON lines, stable event codes), never mixed.

## Testing

`docker/compose.yml` brings up SQL Server 2022 Developer, (with `--profile rabbit`) RabbitMQ 4,
and (with `--profile clickhouse`) RabbitMQ plus ClickHouse reading every event into a queryable
table; all keep their data in volumes. `docker/seed.sql` creates the test database, logins
`svc_app` and `app_user`, procedures and a table type. [docs/END-TO-END.md](docs/END-TO-END.md)
walks through the whole setup - DaffyTee, RabbitMQ and ClickHouse - for demos.

```
docker compose -f docker/compose.yml --profile rabbit up -d
docker exec daffytee-sql-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "DaffyTee!Dev2026" -i /seed/seed.sql

dotnet test                                          # unit tests: parsers, tap pairing, budget, store crypto, commands, the relay on sockets
powershell -File scripts/verify-relay.ps1            # a real DaffyTee: 32 client checks, the Windows TDS 7.1 ODBC driver, then 35 assertions on the events, the log, the commands and the certificate
powershell -File scripts/verify-rabbit.ps1           # capture into RabbitMQ, then an outage under load
powershell -File scripts/verify-linux.ps1            # pack, install in a Linux container, run every check there
```

The scripts run under Windows PowerShell 5.1 or `pwsh`, start their own DaffyTee on their own port
with a throwaway config directory, and assert on what the run must produce.
`scripts/capture-client-init.ps1` is not a check: it connects every client library on the machine
once, runs `SELECT @@VERSION`, and keeps what DaffyTee recorded of each.
[docs/CLIENT-INIT.md](docs/CLIENT-INIT.md) was written from it.

`probes/TeeProbe` is the client-side harness (Microsoft.Data.SqlClient): identity pass-through,
all three encryption modes, refused logins, every data type compared against a direct connection,
10 MB MAX parameters, TVPs, output parameters, errors, multiple result sets, transactions and
savepoints, pooling resets, cancel and timeout, bulk copy, MARS, `USE`, a million-row read,
prepared statements - plus `--bench`, `--load N [--sessions M]` and `--consume URI` to drain and
validate a RabbitMQ queue. `--expect-trust no|yes` adds a check that connects without
`TrustServerCertificate` (Mandatory and Strict), as Power BI does, and expects a refusal or a login.
`--gateway admin|user [--non-admin USER:PASSWORD]` adds the `daffytee.*` checks: answers that agree
with the target (SPID), procedure calls, refusals, a pooled reset arriving on a command, a command
in a transaction, requests that only mention `daffytee` (one past the 64 KB held back), MARS,
Always Encrypted, the admin commands including killing a session, and a non-admin refused them.

Trusting the certificate is checked by hand, because Windows' confirmation needs a person: run
`daffytee cert trust`, answer Yes, run TeeProbe with `--expect-trust yes`, then `daffytee cert
untrust` and `--expect-trust no`. The unit tests never write to a certificate store.

`--transcript DIR` writes a decoded transcript of both directions of every session - DaffyDB's old
capture relay, now able to see inside TLS because DaffyTee terminates it. It shows SQL and data in
the clear and slows sessions; it is a debugging tool.

## Limits

- Clients log in to DaffyTee with a DaffyTee login; Windows (SSPI/Kerberos) and Entra ID logins
  from the client are refused. Toward the target, DaffyTee logs in with a SQL login, or to Azure
  SQL as an Entra ID application (service principal, client secret) - it gets the token itself and
  sends the secret only to Entra ID. Windows authentication toward the target is not supported yet.
- Targets are SQL Server on-premises, in a VM or in a container, and Azure SQL Database (SQL
  logins). A routing redirect from the target is followed by DaffyTee itself, so the client never
  connects around it; one redirect per login. Tested against Azure SQL's Redirect connection
  policy (TDS 7.4 and 8.0); Availability Group read-only routing uses the same mechanism but has
  not been tried against a real availability group.
- Not a Windows service yet; that is planned for a later release.
- Kafka is coming soon; RabbitMQ and the NDJSON file are the sinks today.
- Named instances need their TCP port (`host,port`); SQL Browser lookup is not implemented.
- `Encrypt=Strict` toward a target needs a SQL Server with a proper certificate for TDS 8.0
  connections - a self-generated one is refused by SQL Server itself.
- Distributed (DTC) transactions pass through but are untested.
- `daffytee.*` commands are not answered on MARS connections or inside `sp_executesql`; they reach
  the target, which does not know them.
- Events are held in memory only; a crash or restart loses the undelivered ones (and says so).

## Layout

```
src/DaffyTee/
  Tds/        wire format: framing, PRELOGIN, LOGIN7 (parse and build), the 7.4 TLS shim,
              request and response decoding, SMP (MARS), the certificate and trusting it
  Proxy/      listener, front session (handshake, auth), upstream connector, relay, sessions
  Capture/    the tap, the budget, the pipeline, decoding into events, JSON, SQL rendering
  Sinks/      RabbitMQ, NDJSON file, rolling files
  Security/   the encrypted user store, password hashing, key protection
  Commands/   daffytee.* in T-SQL: recognising them, the catalog, carrying them out (from DaffyDuo)
  Cli/ Repl/  commands and the prompt
tests/DaffyTee.Tests/    unit tests (fixtures are real payloads: SqlClient's, the Windows TDS 7.1 drivers', SQL Server's)
probes/TeeProbe/         the end-to-end client harness
docker/                  test SQL Server and RabbitMQ
NUGET.md                 the package page, including the event contract
LICENSE.txt              MIT License
```

## Licence

[MIT License](LICENSE.txt). Copyright (c) 2026 Scott Traube and TroBeeOne LLC.

You may use, copy, modify and redistribute DaffyTee, in commercial products too. The one
condition: keep the copyright notice above, crediting Scott Traube and TroBeeOne LLC, together
with the MIT License text, in every copy or substantial portion of it. Shipping
[LICENSE.txt](LICENSE.txt) with it does both.

The MIT License applies from version 0.3.2; versions up to 0.3.1 were published under the
Business Source License 1.1. Third-party components keep their own licenses:
[THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt). Contact: [trobee.one](https://trobee.one) ·
[LinkedIn](https://www.linkedin.com/company/trobee-one-llc).

Outside contributions can only be accepted with a contributor agreement, so that TroBeeOne keeps
sole ownership of the code.
DaffyTee implements the open [MS-TDS] specification and contains no Microsoft code; it is not
affiliated with or endorsed by Microsoft. SQL Server is a trademark of Microsoft Corporation;
RabbitMQ is a trademark of Broadcom Inc.; Apache Kafka is a trademark of the Apache Software
Foundation.
