-- Fee variants (MPE-188): a POST may request the same eval for
-- several loan types (FHA vs conventional comparison) — each variant
-- is its own eval row sharing a batch_id. Overrides gain a variant
-- dimension so an FHA override can't leak into the conventional
-- side-by-side. SQLite can't drop a UNIQUE constraint, so
-- fee_overrides is rebuilt with variant in the key.
ALTER TABLE fee_evals ADD COLUMN batch_id TEXT;
ALTER TABLE fee_evals ADD COLUMN variant TEXT NOT NULL DEFAULT '';
CREATE INDEX IF NOT EXISTS ix_fee_evals_batch ON fee_evals (batch_id);

CREATE TABLE fee_overrides_v2 (
    override_id    TEXT PRIMARY KEY,
    application_id TEXT NOT NULL REFERENCES applications(id),
    variant        TEXT NOT NULL DEFAULT '',
    fee_code       TEXT NOT NULL,
    section        TEXT NOT NULL,
    amount         REAL NOT NULL,
    reason         TEXT NOT NULL,
    actor          TEXT NOT NULL,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    stale          INTEGER NOT NULL DEFAULT 0,
    cleared_utc    TEXT,
    UNIQUE (application_id, variant, fee_code, section)
);
INSERT INTO fee_overrides_v2
    (override_id, application_id, variant, fee_code, section,
     amount, reason, actor, created_utc, stale, cleared_utc)
SELECT override_id, application_id, '', fee_code, section,
       amount, reason, actor, created_utc, stale, cleared_utc
FROM fee_overrides;
DROP TABLE fee_overrides;
ALTER TABLE fee_overrides_v2 RENAME TO fee_overrides;
CREATE INDEX IF NOT EXISTS ix_fee_overrides_app
    ON fee_overrides (application_id);
