-- Tenant data-plane mutation event log + conditions (M15 P1).
-- loan_events: append-only, hash-chained mutation log — every field
-- change, override, escalation, and lifecycle transition. PII-class
-- before/after values are blind-index HMACs, never plaintext
-- (invariant 1); non-PII values are plaintext. Rows are never updated
-- or deleted; a tampered row breaks chain verification.
-- Snapshots stay the source of truth — events are additive
-- (invariant 6).
CREATE TABLE IF NOT EXISTS loan_events (
    seq            INTEGER PRIMARY KEY AUTOINCREMENT,
    event_id       TEXT NOT NULL UNIQUE,
    application_id TEXT NOT NULL,
    event_kind     TEXT NOT NULL,          -- field_change|override|escalation|transition|reveal|condition_*
    actor          TEXT NOT NULL,
    entity         TEXT NOT NULL,
    entity_id      TEXT,
    field_path     TEXT,
    field_class    TEXT,                   -- PiiClass name when the field is PII-classed
    before_val     TEXT,                   -- blind-index HMAC for PII class, plaintext otherwise
    after_val      TEXT,
    prev_hash      TEXT NOT NULL,
    hash           TEXT NOT NULL,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_loan_events_app
    ON loan_events (application_id, seq);
CREATE INDEX IF NOT EXISTS ix_loan_events_entity
    ON loan_events (entity, entity_id, seq);

-- Conditions (M15 P1) — tracking items derived from state or created
-- manually. blocking=1 blocks submit/downstream steps; kind names the
-- source dimension for M17/M18 auto-conditions.
CREATE TABLE IF NOT EXISTS conditions (
    seq              INTEGER PRIMARY KEY AUTOINCREMENT,
    condition_id     TEXT NOT NULL UNIQUE,
    application_id   TEXT NOT NULL REFERENCES applications(id),
    kind             TEXT NOT NULL,        -- doc_required|data_validation|distance_check|manual
    state            TEXT NOT NULL,        -- open|closed|waived
    blocking         INTEGER NOT NULL DEFAULT 0,
    linked_entity    TEXT,                 -- e.g. vault doc, party member
    linked_entity_id TEXT,
    title            TEXT NOT NULL,
    detail           TEXT,                 -- internal detail — staff only
    opened_by        TEXT NOT NULL,        -- actor id or 'auto:<source>'
    opened_utc       TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    closed_utc       TEXT,
    close_reason     TEXT,
    waiver_reason    TEXT
);
CREATE INDEX IF NOT EXISTS ix_conditions_app
    ON conditions (application_id, state, seq);
