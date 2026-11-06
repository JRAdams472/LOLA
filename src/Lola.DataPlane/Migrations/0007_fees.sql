-- Tenant fee surface (M8 P2, MPE-183). Three tables:
--   fee_evals   — append-only evaluation record: one row per
--                 enqueued eval, completed in place by the worker.
--                 Rows are never deleted; history is the table.
--   fee_outbox  — the work queue: kind/payload/status/attempts/
--                 next_retry/dlq. The request_hash UNIQUE makes a
--                 duplicate enqueue a no-op (idempotency).
--   fee_items   — one row per fee line of a completed eval. Phase 3
--                 (MPE-184) keys overrides off item_id, so identity
--                 is stable from insertion.
-- Neither evals nor items carry PII — scenario/payload JSON is the
-- PII-free egress contract (invariant 2, firewall-tested).
CREATE TABLE IF NOT EXISTS fee_evals (
    eval_id        TEXT PRIMARY KEY,
    application_id TEXT NOT NULL REFERENCES applications(id),
    adapter        TEXT NOT NULL,
    request_hash   TEXT NOT NULL UNIQUE,
    scenario_json  TEXT NOT NULL,
    status         TEXT NOT NULL DEFAULT 'pending',  -- pending|done|failed
    result_json    TEXT,
    error          TEXT,
    actor          TEXT NOT NULL,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    completed_utc  TEXT
);
CREATE INDEX IF NOT EXISTS ix_fee_evals_app
    ON fee_evals (application_id, created_utc);

CREATE TABLE IF NOT EXISTS fee_items (
    item_id          TEXT PRIMARY KEY,
    eval_id          TEXT NOT NULL REFERENCES fee_evals(eval_id),
    fee_code         TEXT NOT NULL,
    label            TEXT,
    section          TEXT NOT NULL,     -- LE cost block: A|B|C|E|F
    base             REAL NOT NULL,
    adjustments_json TEXT NOT NULL,
    total            REAL NOT NULL,
    created_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_fee_items_eval
    ON fee_items (eval_id);

CREATE TABLE IF NOT EXISTS fee_outbox (
    outbox_id      TEXT PRIMARY KEY,
    kind           TEXT NOT NULL,           -- eval
    eval_id        TEXT NOT NULL REFERENCES fee_evals(eval_id),
    request_hash   TEXT NOT NULL UNIQUE,
    payload_json   TEXT NOT NULL,
    status         TEXT NOT NULL DEFAULT 'pending',  -- pending|done|dlq
    attempts       INTEGER NOT NULL DEFAULT 0,
    next_retry_utc TEXT,
    last_error     TEXT,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_fee_outbox_due
    ON fee_outbox (status, next_retry_utc);
