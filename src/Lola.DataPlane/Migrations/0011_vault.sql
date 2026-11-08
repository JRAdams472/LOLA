-- Tenant borrower-document vault (M10 P1, MPE-200). Two tables:
--   vault_docs        — upload registry: one row per accepted upload,
--                       advanced in place by the scan worker. LOLA
--                       stores references + metadata only — the bytes
--                       live as app-layer ciphertext in the tenant's
--                       MinIO bucket (quarantine/ → clean/ prefixes).
--   vault_scan_outbox — the scan work queue: dedupe by request_hash,
--                       bounded attempts + backoff, dead-letter
--                       instead of silent loss. Same shape as
--                       doc_outbox/fee_outbox.
-- filename is `vault:` ciphertext — uploader filenames routinely
-- carry borrower names/employers (PII-class, invariant 1). wrapped_dek
-- is the per-object AES key wrapped under the tenant transit key —
-- ciphertext too. Every other column is PII-free metadata.
CREATE TABLE IF NOT EXISTS vault_docs (
    vault_doc_id     TEXT PRIMARY KEY,
    application_id   TEXT NOT NULL REFERENCES applications(id),
    classification   TEXT NOT NULL,        -- paystub|w2|bank-statement|id|other
    filename         TEXT NOT NULL,        -- vault: ciphertext (PII-class)
    content_type     TEXT NOT NULL,        -- canonical sniffed type
    size_bytes       INTEGER NOT NULL,
    sha256           TEXT NOT NULL,        -- plaintext hash — download integrity
    wrapped_dek      TEXT NOT NULL,        -- vault: ciphertext (object DEK)
    adapter          TEXT NOT NULL,        -- stub|minio
    s3_key           TEXT NOT NULL,        -- quarantine/<id> → clean/<id>
    status           TEXT NOT NULL DEFAULT 'quarantined',
                     -- quarantined|scanning|clean|infected|unavailable
    error            TEXT,
    uploaded_by      TEXT NOT NULL,
    created_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    scanned_utc      TEXT,
    request_hash     TEXT NOT NULL UNIQUE
);
CREATE INDEX IF NOT EXISTS ix_vault_docs_app
    ON vault_docs (application_id, created_utc);
CREATE INDEX IF NOT EXISTS ix_vault_docs_status
    ON vault_docs (status);

CREATE TABLE IF NOT EXISTS vault_scan_outbox (
    outbox_id      TEXT PRIMARY KEY,
    vault_doc_id   TEXT NOT NULL REFERENCES vault_docs(vault_doc_id),
    request_hash   TEXT NOT NULL UNIQUE,
    status         TEXT NOT NULL DEFAULT 'pending',  -- pending|done|dlq
    attempts       INTEGER NOT NULL DEFAULT 0,
    next_retry_utc TEXT,
    last_error     TEXT,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_vault_scan_outbox_due
    ON vault_scan_outbox (status, next_retry_utc);
