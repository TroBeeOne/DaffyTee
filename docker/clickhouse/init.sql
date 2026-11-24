-- ClickHouse for the DaffyTee demo: every event DaffyTee publishes to RabbitMQ becomes a row here,
-- queryable while it happens. Not part of DaffyTee itself - a consumer, to show what one looks like.
--
-- ClickHouse runs this once, when its data volume is new (docker-entrypoint-initdb.d). To run it
-- again from scratch: docker compose -f docker/compose.yml --profile clickhouse down,
-- docker volume rm daffytee_clickhouse-data, and up again.
--
-- Three parts:
--   events              the table you query: one row per event, the useful fields pulled out,
--                       the whole event kept as JSON in `event`
--   rabbitmq_in         the subscription: a RabbitMQ table bound to the daffytee exchange (every
--                       routing key) through a durable queue of its own. It cannot be queried.
--   rabbitmq_to_events  moves each message from the subscription into `events` as it arrives

CREATE DATABASE IF NOT EXISTS daffytee;

CREATE TABLE IF NOT EXISTS daffytee.events
(
    time          DateTime64(7, 'UTC'),
    type          LowCardinality(String),      -- request.batch, request.rpc, session.start, proxy.heartbeat, ...
    id            String,                      -- the event id (UUIDv7): unique, sorts by time
    source        LowCardinality(String),      -- which DaffyTee: daffytee://host:port
    session_id    Nullable(UInt32),
    seq           Nullable(UInt64),            -- the request's number within its session
    login         String,                      -- the DaffyTee login
    target_login  String,                      -- the SQL Server login it was mapped to
    target        LowCardinality(String),
    spid          Nullable(Int32),
    database      LowCardinality(String),
    app           LowCardinality(String),
    host          LowCardinality(String),
    client        String,
    kind          LowCardinality(String),      -- batch, rpc, txn, bulk
    sql           String,                      -- the batch text, or the statement of an sp_executesql
    proc          String,                      -- the procedure an RPC called
    status        LowCardinality(String),      -- ok, error, cancelled, incomplete
    duration_ms   Nullable(Float64),
    first_byte_ms Nullable(Float64),
    row_count     Nullable(UInt64),
    error_number  Nullable(Int32),
    error_message String,
    redacted      Bool,                        -- a password in it was masked
    reason        String,                      -- why a session ended, or a login was refused
    via           LowCardinality(String),      -- how it arrived: rabbitmq
    event         String                       -- the whole event, as published
)
ENGINE = MergeTree
PARTITION BY toYYYYMMDD(time)
ORDER BY (time, id);

CREATE TABLE IF NOT EXISTS daffytee.rabbitmq_in
(
    event String
)
ENGINE = RabbitMQ
SETTINGS
    rabbitmq_host_port = 'rabbit:5672',
    rabbitmq_username = 'guest',
    rabbitmq_password = 'guest',
    rabbitmq_exchange_name = 'daffytee',
    rabbitmq_exchange_type = 'topic',
    rabbitmq_routing_key_list = '#',
    rabbitmq_queue_base = 'clickhouse',
    rabbitmq_format = 'JSONAsString',
    rabbitmq_flush_interval_ms = 1000;

CREATE MATERIALIZED VIEW IF NOT EXISTS daffytee.rabbitmq_to_events TO daffytee.events AS
SELECT
    parseDateTime64BestEffort(JSONExtractString(event, 'time'), 7)              AS time,
    JSONExtractString(event, 'type')                                             AS type,
    JSONExtractString(event, 'id')                                               AS id,
    JSONExtractString(event, 'source')                                           AS source,
    JSONExtract(event, 'data', 'session', 'id', 'Nullable(UInt32)')              AS session_id,
    JSONExtract(event, 'data', 'seq', 'Nullable(UInt64)')                        AS seq,
    -- a refused login has no session yet; its login, host, app and client sit directly in data
    coalesce(nullIf(JSONExtractString(event, 'data', 'session', 'login'), ''),
             JSONExtractString(event, 'data', 'login'))                          AS login,
    JSONExtractString(event, 'data', 'session', 'targetLogin')                   AS target_login,
    JSONExtractString(event, 'data', 'session', 'target')                        AS target,
    JSONExtract(event, 'data', 'session', 'spid', 'Nullable(Int32)')             AS spid,
    coalesce(nullIf(JSONExtractString(event, 'data', 'session', 'database'), ''),
             JSONExtractString(event, 'data', 'database'))                       AS database,
    coalesce(nullIf(JSONExtractString(event, 'data', 'session', 'app'), ''),
             JSONExtractString(event, 'data', 'app'))                            AS app,
    coalesce(nullIf(JSONExtractString(event, 'data', 'session', 'host'), ''),
             JSONExtractString(event, 'data', 'host'))                           AS host,
    coalesce(nullIf(JSONExtractString(event, 'data', 'session', 'client'), ''),
             JSONExtractString(event, 'data', 'client'))                         AS client,
    JSONExtractString(event, 'data', 'request', 'kind')                          AS kind,
    coalesce(nullIf(JSONExtractString(event, 'data', 'request', 'sql'), ''),
             JSONExtractString(event, 'data', 'request', 'calls', 1, 'sql'))     AS sql,
    JSONExtractString(event, 'data', 'request', 'calls', 1, 'proc')              AS proc,
    JSONExtractString(event, 'data', 'response', 'status')                       AS status,
    JSONExtract(event, 'data', 'durationMs', 'Nullable(Float64)')                AS duration_ms,
    JSONExtract(event, 'data', 'response', 'firstByteMs', 'Nullable(Float64)')   AS first_byte_ms,
    JSONExtract(event, 'data', 'response', 'rowCount', 'Nullable(UInt64)')       AS row_count,
    JSONExtract(event, 'data', 'response', 'errors', 1, 'number', 'Nullable(Int32)') AS error_number,
    JSONExtractString(event, 'data', 'response', 'errors', 1, 'message')         AS error_message,
    JSONExtractBool(event, 'data', 'request', 'redacted')                        AS redacted,
    JSONExtractString(event, 'data', 'reason')                                   AS reason,
    'rabbitmq'                                                                   AS via,
    event
FROM daffytee.rabbitmq_in;
