-- Vendor adapter seam (M13 P1, MPE-226). Two tables:
--   vendor_orders — order registry: one row per placed order. Status
--                   machine: ordered → submitted → completed|failed.
--                   `request_json` and `result_json` are `vault:`
--                   ciphertext — order payloads and reports carry
--                   PII by nature (a credit pull needs SSN; the
--                   report IS the borrower's file). Every other
--                   column is PII-free metadata.
--   vendor_outbox — the send work queue: dedupe by request_hash,
--                   bounded attempts + backoff, dead-letter instead
--                   of silent loss. Same shape as
--                   fee_outbox/doc_outbox/vault_scan_outbox.
CREATE TABLE IF NOT EXISTS vendor_orders (
    order_id       TEXT PRIMARY KEY,
    application_id TEXT NOT NULL REFERENCES applications(id),
    kind           TEXT NOT NULL,          -- credit|voe|voi|flood|fraud|aus
    adapter        TEXT NOT NULL,          -- stub|<vendor>
    external_id    TEXT,                   -- vendor's receipt/order id
    status         TEXT NOT NULL DEFAULT 'ordered',
                   -- ordered|submitted|completed|failed
    request_json   TEXT NOT NULL,          -- vault: ciphertext (PII request)
    result_json    TEXT,                   -- vault: ciphertext (PII report)
    error          TEXT,
    ordered_by     TEXT NOT NULL,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    submitted_utc  TEXT,
    completed_utc  TEXT,
    request_hash   TEXT NOT NULL UNIQUE
);
CREATE INDEX IF NOT EXISTS ix_vendor_orders_app
    ON vendor_orders (application_id, created_utc);
CREATE INDEX IF NOT EXISTS ix_vendor_orders_status
    ON vendor_orders (status);
CREATE INDEX IF NOT EXISTS ix_vendor_orders_external
    ON vendor_orders (kind, external_id);

CREATE TABLE IF NOT EXISTS vendor_outbox (
    outbox_id      TEXT PRIMARY KEY,
    order_id       TEXT NOT NULL REFERENCES vendor_orders(order_id),
    request_hash   TEXT NOT NULL UNIQUE,
    status         TEXT NOT NULL DEFAULT 'pending',  -- pending|done|dlq
    attempts       INTEGER NOT NULL DEFAULT 0,
    next_retry_utc TEXT,
    last_error     TEXT,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_vendor_outbox_due
    ON vendor_outbox (status, next_retry_utc);
