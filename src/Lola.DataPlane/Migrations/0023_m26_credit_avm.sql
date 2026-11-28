-- M26 — credit tri-merge reports + AVM results. Append-only: rows are
-- never updated or deleted; re-order inserts new rows (invariant 4).
-- Merged tradelines + raw bureau responses are PII-bearing and live
-- ONLY inside the tenant boundary — never in loan events, logs, or
-- egress (invariants 1/3). Late counts are credit attributes (not
-- identity data) kept as queryable columns for pricing adjustments.

CREATE TABLE credit_reports (
    id                TEXT PRIMARY KEY,
    application_id    TEXT NOT NULL,
    order_id          TEXT,                       -- vendor order correlation
    bureaus_count     INTEGER NOT NULL,           -- how many bureau files merged
    score_equifax     INTEGER,
    score_transunion  INTEGER,
    score_experian    INTEGER,
    score_model_equifax    TEXT,                  -- e.g. 'fico-10t', 'vantagescore-4.0'
    score_model_transunion TEXT,
    score_model_experian   TEXT,
    representative_score INTEGER,               -- mid-of-three / lower-of-two
    representative_model TEXT,
    lates_30          INTEGER NOT NULL DEFAULT 0, -- 24-month lookback, merged
    lates_60          INTEGER NOT NULL DEFAULT 0,
    lates_90          INTEGER NOT NULL DEFAULT 0,
    merged_json       TEXT NOT NULL,              -- merged tradelines + scores (ciphertext boundary)
    raw_json          TEXT,                       -- per-bureau raw parsed files (tenant boundary only)
    report_hash       TEXT NOT NULL,              -- sha256 of merged_json
    created_utc       TEXT NOT NULL,
    created_by        TEXT NOT NULL
);
CREATE INDEX idx_credit_reports_app ON credit_reports(application_id, created_utc);

CREATE TABLE avm_results (
    id                TEXT PRIMARY KEY,
    application_id    TEXT NOT NULL,
    order_id          TEXT,                       -- vendor order correlation
    value             REAL,                       -- point valuation
    confidence        REAL,                       -- confidence score / 100-scale
    fsd               REAL,                       -- forecast standard deviation when provided
    valuation_date    TEXT,
    model             TEXT,                       -- vendor model name/version
    raw_json          TEXT,                       -- raw vendor response (tenant boundary only)
    result_hash       TEXT NOT NULL,
    created_utc       TEXT NOT NULL,
    created_by        TEXT NOT NULL
);
CREATE INDEX idx_avm_results_app ON avm_results(application_id, created_utc);
