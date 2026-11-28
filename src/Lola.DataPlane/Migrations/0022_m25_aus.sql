-- M25 — AUS (DU/LPA) results + findings. Append-only: rows are never
-- updated or deleted; re-submission inserts new rows (invariant 4).
-- Raw response XML is PII-bearing and lives ONLY inside the tenant
-- boundary — never in loan events, logs, or egress.

CREATE TABLE aus_results (
    id                TEXT PRIMARY KEY,
    application_id    TEXT NOT NULL,
    vendor            TEXT NOT NULL,              -- 'du' | 'lpa'
    order_id          TEXT,                       -- vendor order correlation
    case_id           TEXT,                       -- DU casefile / LPA key
    recommendation    TEXT NOT NULL,              -- normalized recommendation
    risk_class        TEXT,                       -- vendor risk classification
    response_xml      TEXT,                       -- raw wire response (tenant boundary only)
    response_hash     TEXT NOT NULL,              -- sha256 of response_xml
    findings_count    INTEGER NOT NULL DEFAULT 0,
    created_utc       TEXT NOT NULL,
    created_by        TEXT NOT NULL
);
CREATE INDEX idx_aus_results_app ON aus_results(application_id, created_utc);

CREATE TABLE aus_findings (
    id                TEXT PRIMARY KEY,
    result_id         TEXT NOT NULL REFERENCES aus_results(id),
    vendor_code       TEXT NOT NULL,              -- vendor-native finding/message code
    category          TEXT NOT NULL,              -- normalized: credit|income|asset|collateral|eligibility|other
    severity          TEXT NOT NULL,              -- normalized: info|warning|required|fatal
    message           TEXT NOT NULL,              -- vendor message text (no borrower PII)
    condition_id      TEXT,                       -- linked condition when auto-populated
    created_utc       TEXT NOT NULL
);
CREATE INDEX idx_aus_findings_result ON aus_findings(result_id);
