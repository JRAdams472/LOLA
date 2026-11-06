-- Fee overrides (M8 P3, MPE-184). An override is an application-level
-- fact keyed by (application_id, fee_code, section) — NOT a row on
-- fee_items — so it survives re-evaluation: the merge matches new
-- eval items by key, and overrides whose fee no longer exists are
-- marked stale, never dropped (acceptance: surface, don't delete).
-- The audit event (fee_override / fee_override_clear) carries actor
-- + reason + amount; this table is operational current-state.
CREATE TABLE IF NOT EXISTS fee_overrides (
    override_id    TEXT PRIMARY KEY,
    application_id TEXT NOT NULL REFERENCES applications(id),
    fee_code       TEXT NOT NULL,
    section        TEXT NOT NULL,
    amount         REAL NOT NULL,
    reason         TEXT NOT NULL,
    actor          TEXT NOT NULL,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    stale          INTEGER NOT NULL DEFAULT 0,  -- 1 = orphaned by re-eval
    cleared_utc    TEXT,
    UNIQUE (application_id, fee_code, section)
);
CREATE INDEX IF NOT EXISTS ix_fee_overrides_app
    ON fee_overrides (application_id);
