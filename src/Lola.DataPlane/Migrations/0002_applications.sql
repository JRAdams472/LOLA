-- Tenant data-plane applications schema (M5 P1, ADR-0006). Canonical
-- versions live as JSON1 payloads; relational index columns cover
-- queryable/regulated fields. PII columns hold `vault:` ciphertext;
-- searchable PII emits `idx:` blind-index tokens — never plaintext.
CREATE TABLE IF NOT EXISTS applications (
    id               TEXT PRIMARY KEY,
    status           TEXT NOT NULL DEFAULT 'draft'
                     CHECK (status IN ('draft','submitted','in_review','decisioned')),
    pipeline_step    TEXT NOT NULL DEFAULT 'POSIntake',
    borrower_user_id TEXT,               -- own-draft scoping (M12 seam)
    current_version  INTEGER NOT NULL DEFAULT 0,
    trid_clock_start TEXT,               -- set once, immutable
    created_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    updated_utc      TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);

CREATE TABLE IF NOT EXISTS application_versions (
    id              TEXT PRIMARY KEY,
    application_id  TEXT NOT NULL REFERENCES applications(id),
    version         INTEGER NOT NULL,
    status_at_write TEXT NOT NULL,
    payload         TEXT NOT NULL,       -- JSON1 doc, PII members ciphertext
    submitted_utc   TEXT,                -- non-NULL = immutable snapshot
    prev_version_id TEXT,
    written_by      TEXT NOT NULL,
    created_utc     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    UNIQUE (application_id, version)
);
CREATE INDEX IF NOT EXISTS ix_appver_app ON application_versions (application_id, version);

CREATE TABLE IF NOT EXISTS application_parties (
    id             TEXT PRIMARY KEY,
    application_id TEXT NOT NULL REFERENCES applications(id),
    party_index    INTEGER NOT NULL,
    role           TEXT NOT NULL,
    first_name_ct  TEXT, middle_name_ct TEXT, last_name_ct TEXT,
    suffix_ct      TEXT, ssn_ct TEXT, dob_ct TEXT,
    email_ct       TEXT, phone_ct TEXT,
    UNIQUE (application_id, party_index)
);
CREATE INDEX IF NOT EXISTS ix_parties_app ON application_parties (application_id);

CREATE TABLE IF NOT EXISTS application_blind_idx (
    application_id TEXT NOT NULL REFERENCES applications(id),
    party_index    INTEGER NOT NULL,
    field          TEXT NOT NULL,
    idx            TEXT NOT NULL,
    PRIMARY KEY (application_id, party_index, field)
);
CREATE INDEX IF NOT EXISTS ix_blind_idx ON application_blind_idx (idx);
