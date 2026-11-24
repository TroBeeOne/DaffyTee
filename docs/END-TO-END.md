# DaffyTee end to end: SQL Server, RabbitMQ and ClickHouse

A complete local setup for demonstrations and full validation: clients go through DaffyTee to a
real SQL Server, every request goes to RabbitMQ, and ClickHouse reads it from there into a table
you can query while it happens. Not part of the NuGet package; ClickHouse is here only to show
what a consumer looks like.

```
SSMS / app / load generator
        |
        v
   DaffyTee :1433  ------------------------------->  SQL Server (Docker) :14333
        |
        | every event
        v
   RabbitMQ exchange "daffytee"  ---> queue "clickhouse" ---> ClickHouse table daffytee.events
                                 \--> queue "peek" (optional, for reading messages in RabbitMQ)
```

## Addresses and logins

Throwaway passwords for local containers, nothing else.

| What | Where | Login |
|---|---|---|
| DaffyTee | `127.0.0.1,1433` | the DaffyTee login you create below (`demo`) |
| SQL Server, direct | `127.0.0.1,14333` | `sa` / `DaffyTee!Dev2026`; `svc_app` / `Svc!App2026` |
| RabbitMQ management | http://localhost:15672 | `guest` / `guest` |
| RabbitMQ (AMQP) | `amqp://localhost:5672/` | `guest` / `guest` |
| ClickHouse SQL console | http://localhost:8123/play | `daffytee` / `DaffyTee!Dev2026` |

All commands below run in PowerShell from the root of your clone of this repository.
If `docker` is not found, use its full path: `& "C:\Program Files\Docker\Docker\resources\bin\docker.exe"`.

## 1. Start the containers

```powershell
docker compose -f docker/compose.yml --profile clickhouse up -d --wait
```

This starts all three: SQL Server, RabbitMQ and ClickHouse (the `clickhouse` profile includes
RabbitMQ). `--wait` returns once all three report healthy. The first run downloads the three
images (about 4 GB on disk); if the SQL Server download fails with `EOF` from mcr.microsoft.com,
run it again.

**First time only** (or after a wipe), create the test database and logins:

```powershell
docker exec daffytee-sql-1 /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "DaffyTee!Dev2026" -i /seed/seed.sql
```

The containers keep their data in Docker volumes and restart when Docker does: databases, queues
and ClickHouse rows survive a reboot. Check them at any time:

```powershell
docker compose -f docker/compose.yml --profile clickhouse ps
```

## 2. Set up DaffyTee (once)

```powershell
dotnet tool install -g DaffyTee      # or: dotnet tool update -g DaffyTee
```

To run the code in this repository instead, use `dotnet run --project src/DaffyTee --` wherever
these steps say `daffytee`.

**Map a DaffyTee login** to the test SQL Server:

```powershell
daffytee user add demo --target "Server=tcp:127.0.0.1,14333;User Id=svc_app;Initial Catalog=DaffyTeeTest;TrustServerCertificate=True"
```

It asks for a DaffyTee password for `demo` (twice; choose one), then for the target password:
`Svc!App2026`. It logs in to SQL Server before saving anything.

**Store the RabbitMQ connection:**

```powershell
daffytee config rabbit amqp://guest@localhost:5672/
```

The password is `guest`. It connects to RabbitMQ before saving, and the URI is stored encrypted.

## 3. Start DaffyTee

```powershell
daffytee --sink rabbit --sql-log "sql-{date}.log"
```

- Keep the quotes around `sql-{date}.log`.
- If a local SQL Server already listens on 1433, add `--port 14330` and connect to `127.0.0.1,14330`.
- To read the messages themselves in RabbitMQ (step 5), add `--rabbit-queue peek`.

At the `daffytee>` prompt:

| Command | Shows |
|---|---|
| `:status` | the sink should say `healthy`, with a recent delivery |
| `:watch` | every request as runnable T-SQL as it completes; any key stops it |
| `:sessions` | who is connected, with each session's SPID on SQL Server |
| `:quit` | stop, after delivering what is queued |

## 4. Connect a client

**SSMS:** Server name `127.0.0.1,1433`, Authentication **SQL Server Authentication**, Login `demo`
and its DaffyTee password, Encryption **Mandatory**, and tick **Trust server certificate**.

**Connection string** (applications, a load generator):

```
Server=tcp:127.0.0.1,1433;Database=DaffyTeeTest;User Id=demo;Password=...;Encrypt=True;TrustServerCertificate=True
```

`SELECT SUSER_SNAME()` answers `svc_app`: the session really runs on SQL Server as the mapped
login. Anything a load generator does through this connection shows up in `:watch`, RabbitMQ and
ClickHouse within about a second.

## 5. Watch it in RabbitMQ

Open http://localhost:15672 and log in as `guest` / `guest`.

- **Overview** - the *Message rates* chart shows events being published as the load runs.
- **Exchanges -> `daffytee`** - the rate in and out, and under *Bindings* who is subscribed:
  `daffytee_clickhouse_bridge` is ClickHouse's subscription (and `peek`, if you added it).
- **Queues and Streams -> `clickhouse`** - one consumer (ClickHouse). *Ready* stays near 0,
  because ClickHouse keeps up.

**Resilience demo:** stop ClickHouse and watch *Ready* on the `clickhouse` queue climb while the
load keeps running, then start it and watch it drain. Nothing is lost, and the clients never
notice:

```powershell
docker stop daffytee-clickhouse-1
docker start daffytee-clickhouse-1
```

**Reading the messages:** ClickHouse takes them as fast as they arrive, so nothing sits in its
queue to look at. Run DaffyTee with `--rabbit-queue peek` and a second queue, `peek`, keeps a copy
of everything. Open **Queues and Streams -> `peek` -> Get messages**, leave *Ack mode* on **Nack
message requeue true** (so they stay in the queue), set *Messages* to 5, and click **Get
Message(s)**. Each one is a CloudEvents JSON document; the routing key is the event type.

`peek` keeps collecting - even when DaffyTee later runs without `--rabbit-queue`, since its
binding stays - until you remove it. When the demo is done, open the queue and click **Purge
Messages**, or **Delete Queue**.

## 6. Query it in ClickHouse

Open http://localhost:8123/play, enter `daffytee` and `DaffyTee!Dev2026` in the user and password
boxes at the top right, type a query and press **Ctrl+Enter**. Rows arrive about a second after the
request finishes.

Any SQL client works too - in DBeaver: new connection, **ClickHouse**, host `localhost`, port
`8123`, database `daffytee`, the same user and password.

One table, `daffytee.events`, one row per event:

| Column | |
|---|---|
| `time`, `type`, `id`, `source` | when, what (`request.batch`, `request.rpc`, `session.start`, `session.end`, `session.login_failed`, `proxy.heartbeat` ...), the event id, which DaffyTee |
| `login`, `target_login`, `target`, `spid`, `database`, `app`, `host`, `client` | who and where |
| `session_id`, `seq` | DaffyTee's session id, and the request's number within it |
| `kind`, `sql`, `proc` | batch or rpc; the SQL text (for sp_executesql, its statement); the procedure an RPC called |
| `status`, `duration_ms`, `first_byte_ms`, `row_count` | ok / error / cancelled / incomplete; timing; rows of the last statement |
| `error_number`, `error_message` | the first error, if any |
| `redacted` | a password in the request was masked |
| `reason` | why a session ended, or why a login was refused |
| `event` | the whole event as published, for anything not pulled out into a column |

The schema is in `docker/clickhouse/init.sql`.

**The live feed** - the latest requests:

```sql
SELECT formatDateTime(time, '%H:%i:%S') AS at, login, app,
       if(kind = 'rpc', proc, kind) AS call, left(sql, 80) AS sql,
       status, round(duration_ms, 2) AS ms, row_count
FROM daffytee.events
WHERE type LIKE 'request.%'
ORDER BY time DESC
LIMIT 20
```

**Activity per minute, by application:**

```sql
SELECT toStartOfMinute(time) AS minute, app, count() AS requests,
       round(avg(duration_ms), 2) AS avg_ms, countIf(status = 'error') AS errors
FROM daffytee.events
WHERE type LIKE 'request.%'
GROUP BY minute, app
ORDER BY minute DESC, requests DESC
```

**The slowest statements** - `normalizeQuery` replaces literal values with `?`, so the same
statement with different values counts as one:

```sql
SELECT left(normalizeQuery(sql), 100) AS statement, count() AS runs,
       round(avg(duration_ms), 2) AS avg_ms, round(quantile(0.95)(duration_ms), 2) AS p95_ms,
       round(max(duration_ms), 2) AS max_ms
FROM daffytee.events
WHERE type LIKE 'request.%' AND sql != ''
GROUP BY statement
ORDER BY avg_ms DESC
LIMIT 20
```

**Stored procedure calls:**

```sql
SELECT proc, count() AS calls, round(avg(duration_ms), 2) AS avg_ms
FROM daffytee.events
WHERE kind = 'rpc' AND NOT startsWith(proc, 'sp_')
GROUP BY proc
ORDER BY calls DESC
```

**Errors:**

```sql
SELECT formatDateTime(time, '%H:%i:%S') AS at, login, app, error_number, error_message, left(sql, 80) AS sql
FROM daffytee.events
WHERE status = 'error'
ORDER BY time DESC
LIMIT 20
```

**Who connects, as whom:**

```sql
SELECT login, target_login, app, host, count() AS sessions
FROM daffytee.events
WHERE type = 'session.start'
GROUP BY login, target_login, app, host
ORDER BY sessions DESC
```

**Refused logins:**

```sql
SELECT formatDateTime(time, '%H:%i:%S') AS at, login, reason, client, app
FROM daffytee.events
WHERE type = 'session.login_failed'
ORDER BY time DESC
```

**Masked passwords** - the statement is there, the password is not:

```sql
SELECT time, login, sql FROM daffytee.events WHERE redacted ORDER BY time DESC
```

**DaffyTee's own health** - a heartbeat every 60 seconds:

```sql
SELECT formatDateTime(time, '%H:%i:%S') AS at,
       JSONExtractUInt(event, 'data', 'sessions')  AS sessions,
       JSONExtractUInt(event, 'data', 'captured')  AS captured,
       JSONExtractUInt(event, 'data', 'published') AS published,
       JSONExtractUInt(event, 'data', 'dropped')   AS dropped,
       JSONExtractString(event, 'data', 'sinkState') AS sink
FROM daffytee.events
WHERE type = 'proxy.heartbeat'
ORDER BY time DESC
LIMIT 10
```

**One whole event**, as DaffyTee published it:

```sql
SELECT event FROM daffytee.events WHERE type = 'request.rpc' ORDER BY time DESC LIMIT 1
```

## 7. Clear things out

**ClickHouse** - in the SQL console:

| To remove | Run |
|---|---|
| every row | `TRUNCATE TABLE daffytee.events` |
| one day (days are partitions, `YYYYMMDD`) | `ALTER TABLE daffytee.events DROP PARTITION 20260925` |
| everything older than an hour | `DELETE FROM daffytee.events WHERE time < now() - INTERVAL 1 HOUR` |

New events keep arriving after any of these; the subscription is untouched.

**Start ClickHouse from scratch** - after changing `docker/clickhouse/init.sql`, say. The schema
script runs only on a new data volume, so remove the volume:

```powershell
docker compose -f docker/compose.yml --profile clickhouse rm -sf clickhouse
docker volume rm daffytee_clickhouse-data
docker compose -f docker/compose.yml --profile clickhouse up -d --wait
```

**RabbitMQ** - in the management UI, **Queues and Streams -> queue -> Purge Messages**: `peek`
when you are done reading it, or `clickhouse` if ClickHouse was down and you do not want the
backlog loaded.

**The SQL log** - delete the `sql-*.log` files in the folder DaffyTee was started from.

## 8. Stop

- **DaffyTee:** `:quit` at its prompt, or Ctrl+C.
- **Containers, keeping everything:** `docker compose -f docker/compose.yml --profile clickhouse stop`,
  and `start` to bring them back. `down` removes the containers but keeps the data too.
- **Containers and all their data:** `docker compose -f docker/compose.yml --profile clickhouse down -v`.
  SQL Server then needs the seed script again (step 1).

## When something does not show up

- **Nothing in ClickHouse:** `:status` at the DaffyTee prompt should say the sink is `healthy`;
  in RabbitMQ, the `clickhouse` queue should have 1 consumer. ClickHouse's own log:
  `docker logs daffytee-clickhouse-1` (lines about IPv6 `[::]` addresses are harmless).
- **`user add` fails on the certificate:** keep `TrustServerCertificate=True` in the target
  connection string; the container's SQL Server certificate is self-signed.
- **DaffyTee cannot listen on 1433:** something else has the port - use `--port 14330`.
- **ClickHouse login fails:** the user is `daffytee`, not `default`.
- **A container is missing after a Docker update:** run step 1 again. The seed script is safe to
  run twice.
