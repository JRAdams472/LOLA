-- Rate locks (M7 P3, MPE-131, invariant 6): one row per lock —
-- re-locking after unlock appends a NEW row so the lock history is
-- preserved. `frozen_paths` is the canonical pricing-input catalog
-- set; UpdateDraft/CorrectApplication reject patch hits on them
-- with 409 while a lock is 'active'.
CREATE TABLE IF NOT EXISTS rate_locks (
    lock_id        TEXT PRIMARY KEY,
    application_id TEXT NOT NULL REFERENCES applications(id),
    eval_id        TEXT NOT NULL REFERENCES pricing_evals(eval_id),
    product_code   TEXT NOT NULL,
    rate           REAL NOT NULL,
    price          REAL,
    lock_term_days INTEGER NOT NULL,
    investor_name  TEXT,
    locked_utc     TEXT NOT NULL,
    expires_utc    TEXT NOT NULL,
    actor          TEXT NOT NULL,
    status         TEXT NOT NULL DEFAULT 'active',  -- active|unlocked
    frozen_paths   TEXT NOT NULL,                   -- JSON array
    unlock_reason  TEXT,
    unlocked_utc   TEXT,
    unlocked_by    TEXT
);
CREATE INDEX IF NOT EXISTS ix_rate_locks_app
    ON rate_locks (application_id, status);
