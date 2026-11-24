-- TRID tolerance engine (M21 P1, MPE-297). Reg Z §1026.19(e)(3):
-- every LE/CD fee lands in the 0% / 10% / unlimited variance bucket.
--
--   tolerance_classes   — fee_code → bucket registry. Unknown codes
--                         fail closed to 0% at classify time.
--   fee_items           — gains paid_to + selected_from_list, the two
--                         classification inputs the adapter records.
--   disclosure_versions — binds each generated le/cd package doc to the
--                         fee_evals snapshot it rendered; seq orders
--                         versions, reset_of_version_id links a re-issue
--                         to the baseline it rebases.
--   tolerance_resets    — changed-circumstance resets: which fees got a
--                         new baseline and under which statutory reason.
--
-- documents.kind has no CHECK — 'cd-package' is additive with no
-- table rebuild.

CREATE TABLE IF NOT EXISTS tolerance_classes (
    fee_code     TEXT PRIMARY KEY,
    bucket       TEXT NOT NULL CHECK (bucket IN ('zero','ten_percent','unlimited')),
    shoppable    INTEGER NOT NULL DEFAULT 0,   -- borrower may shop for this service
    notes        TEXT
);

-- Seed the shipped Zara fee codes. Bucket per §1026.19(e)(3):
--   zero        — lender/affiliate charges + can't-shop services + transfer taxes
--   ten_percent — recording fees + shoppable services picked from the written list
--   unlimited   — borrower-shopped-off-list, prepaid interest, escrows
INSERT INTO tolerance_classes (fee_code, bucket, shoppable) VALUES
    ('origination',  'zero',        0),
    ('credit-report','zero',        0),
    ('appraisal',    'ten_percent', 1),
    ('transfer-tax', 'zero',        0);

ALTER TABLE fee_items ADD COLUMN paid_to TEXT;
ALTER TABLE fee_items ADD COLUMN selected_from_list INTEGER;

CREATE TABLE IF NOT EXISTS disclosure_versions (
    version_id          TEXT PRIMARY KEY,
    application_id      TEXT NOT NULL REFERENCES applications(id),
    kind                TEXT NOT NULL CHECK (kind IN ('le','cd')),
    eval_id             TEXT NOT NULL REFERENCES fee_evals(eval_id),
    doc_id              TEXT REFERENCES documents(doc_id),
    seq                 INTEGER NOT NULL,
    reset_of_version_id TEXT REFERENCES disclosure_versions(version_id),
    created_utc         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    UNIQUE (application_id, kind, seq)
);
CREATE INDEX IF NOT EXISTS ix_disclosure_versions_app
    ON disclosure_versions (application_id, kind);

CREATE TABLE IF NOT EXISTS tolerance_resets (
    reset_id                TEXT PRIMARY KEY,
    application_id          TEXT NOT NULL REFERENCES applications(id),
    reason_code             TEXT NOT NULL CHECK (reason_code IN
        ('changed_info','borrower_request','rate_lock',
         'extraordinary','eligibility','delayed_settlement')),
    affected_fee_codes_json TEXT NOT NULL,   -- non-empty JSON array
    rebased_version_id      TEXT REFERENCES disclosure_versions(version_id),
    actor                   TEXT NOT NULL,
    created_utc             TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_tolerance_resets_app
    ON tolerance_resets (application_id);
