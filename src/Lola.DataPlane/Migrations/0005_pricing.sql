-- Tenant pricing surface (M7 P2, MPE-130). Two tables:
--   pricing_evals   — append-only evaluation record: one row per
--                     enqueued eval, completed in place by the worker.
--                     Rows are never deleted; history is the table.
--   pricing_outbox  — the work queue: kind/payload/status/attempts/
--                     next_retry/dlq. The request_hash UNIQUE makes a
--                     duplicate enqueue a no-op (idempotency).
-- Neither table carries PII — scenario/payload JSON is the PII-free
-- egress contract (invariant 2, firewall-tested).
CREATE TABLE IF NOT EXISTS pricing_evals (
    eval_id        TEXT PRIMARY KEY,
    application_id TEXT NOT NULL REFERENCES applications(id),
    adapter        TEXT NOT NULL,
    as_of          TEXT,
    request_hash   TEXT NOT NULL UNIQUE,
    scenario_json  TEXT NOT NULL,
    status         TEXT NOT NULL DEFAULT 'pending',  -- pending|done|failed
    result_json    TEXT,
    error          TEXT,
    actor          TEXT NOT NULL,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    completed_utc  TEXT
);
CREATE INDEX IF NOT EXISTS ix_pricing_evals_app
    ON pricing_evals (application_id, created_utc);

CREATE TABLE IF NOT EXISTS pricing_outbox (
    outbox_id      TEXT PRIMARY KEY,
    kind           TEXT NOT NULL,           -- eval|reval
    eval_id        TEXT NOT NULL REFERENCES pricing_evals(eval_id),
    request_hash   TEXT NOT NULL UNIQUE,
    payload_json   TEXT NOT NULL,
    status         TEXT NOT NULL DEFAULT 'pending',  -- pending|done|dlq
    attempts       INTEGER NOT NULL DEFAULT 0,
    next_retry_utc TEXT,
    last_error     TEXT,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_pricing_outbox_due
    ON pricing_outbox (status, next_retry_utc);
