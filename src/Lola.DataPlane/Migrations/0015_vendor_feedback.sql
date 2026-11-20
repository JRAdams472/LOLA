-- Tenant data-plane vendor feedback ledger (M17 P1).
-- vendor_feedback: the dedupe + receipt ledger for signed validation
-- webhooks (ADR-0007 ingress). Feedback has no order row to dedupe
-- against, so the ledger lives tenant-side next to the conditions it
-- guards. UNIQUE (source, feedback_id) makes replays insert-or-ignore
-- no-ops across sources whose id spaces may overlap. Rows are
-- metadata only — no PII columns exist here (invariant 2).
CREATE TABLE IF NOT EXISTS vendor_feedback (
    seq              INTEGER PRIMARY KEY AUTOINCREMENT,
    feedback_id      TEXT NOT NULL,
    source           TEXT NOT NULL,          -- mpex|isppr|vendor kind key
    application_id   TEXT NOT NULL REFERENCES applications(id),
    finding          TEXT NOT NULL,          -- data_error|missing_field|exception|cleared
    severity         TEXT NOT NULL,          -- info|warning|error
    linked_entity    TEXT,                   -- condition|vault doc|party member
    linked_entity_id TEXT,
    received_utc     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    UNIQUE (source, feedback_id)
);
CREATE INDEX IF NOT EXISTS ix_vendor_feedback_app
    ON vendor_feedback (application_id, seq);
