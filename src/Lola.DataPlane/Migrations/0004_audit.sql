-- Tenant data-plane audit trail (M6 P1, invariant 4). Append-only,
-- hash-chained: every PII read/write is attributed to actor + entity +
-- field classes + reason — never values. Rows are never updated or
-- deleted; a tampered row breaks the chain verification.
CREATE TABLE IF NOT EXISTS audit_events (
    seq           INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id      TEXT NOT NULL UNIQUE,
    actor         TEXT NOT NULL,
    action        TEXT NOT NULL,
    entity        TEXT NOT NULL,
    entity_id     TEXT,
    field_classes TEXT NOT NULL,        -- JSON array of PiiClass names
    reason        TEXT,
    prev_hash     TEXT NOT NULL,
    hash          TEXT NOT NULL,
    created_utc   TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_audit_entity
    ON audit_events (entity, entity_id, seq);
