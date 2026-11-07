-- Tenant document surface (M9 P2, MPE-190). Two tables:
--   documents  — append-only package registry: one row per enqueued
--                generation, completed in place by the worker. LOLA
--                stores references only (vendor request id + filename)
--                — package bytes stay ISPPr-side behind presigned URLs.
--   doc_outbox — the work queue: kind/payload/status/attempts/
--                next_retry/dlq. request_hash UNIQUE makes a duplicate
--                enqueue a no-op (idempotency).
-- payload_json is `vault:` ciphertext — the disclosure payload carries
-- template-required PII, so it must be encrypted at rest like the
-- application_versions payload (invariant 1). The row's other columns
-- are PII-free metadata.
CREATE TABLE IF NOT EXISTS documents (
    doc_id           TEXT PRIMARY KEY,
    application_id   TEXT NOT NULL REFERENCES applications(id),
    kind             TEXT NOT NULL,          -- le-package|adverse-action
    package_name     TEXT NOT NULL,
    adapter          TEXT NOT NULL,
    isppr_request_id TEXT,                   -- vendor request id once submitted
    request_hash     TEXT NOT NULL UNIQUE,
    status           TEXT NOT NULL DEFAULT 'pending',  -- pending|generating|completed|failed
    filename         TEXT,
    error            TEXT,
    actor            TEXT NOT NULL,
    created_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    completed_utc    TEXT
);
CREATE INDEX IF NOT EXISTS ix_documents_app
    ON documents (application_id, created_utc);

CREATE TABLE IF NOT EXISTS doc_outbox (
    outbox_id      TEXT PRIMARY KEY,
    kind           TEXT NOT NULL,           -- generate
    doc_id         TEXT NOT NULL REFERENCES documents(doc_id),
    request_hash   TEXT NOT NULL UNIQUE,
    payload_json   TEXT NOT NULL,           -- vault: ciphertext
    status         TEXT NOT NULL DEFAULT 'pending',  -- pending|done|dlq
    attempts       INTEGER NOT NULL DEFAULT 0,
    next_retry_utc TEXT,
    last_error     TEXT,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_doc_outbox_due
    ON doc_outbox (status, next_retry_utc);
