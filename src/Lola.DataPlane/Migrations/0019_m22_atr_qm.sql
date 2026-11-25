-- ATR/QM + high-cost engine (M22 P1, MPE-306). Reg Z §1026.43/32/35 +
-- HUD QM (24 CFR 203.19) + state high-cost triggers.
--
--   pf_classes            — fee_code → points-and-fees registry
--                           (§1026.32(b)(1)). Unknown codes fail closed
--                           to 'counted' at classify time.
--   apor_rates            — FFIEC Average Prime Offer Rate table; each
--                           atr_results row records the APOR applied.
--   state_high_cost_rules — per-state high-cost triggers.
--   atr_results           — append-only evaluation rows (the read
--                           surface; verdicts also land as loan_events).

CREATE TABLE IF NOT EXISTS pf_classes (
    fee_code  TEXT PRIMARY KEY,
    category  TEXT NOT NULL CHECK (category IN ('counted','excluded','discount_point')),
    notes     TEXT
);

-- Seed the shipped Zara fee codes. §1026.32(b)(1): origination charges
-- and discount points count; bona fide third-party real-estate charges
-- and taxes are excluded.
INSERT INTO pf_classes (fee_code, category, notes) VALUES
    ('origination',    'counted',        'loan origination charge — §1026.32(b)(1)(i)'),
    ('discount-points','discount_point', 'discount points — counted unless bona fide (§1026.32(b)(1)(i)(F))'),
    ('credit-report',  'excluded',       'bona fide third-party charge — §1026.32(b)(1) exclusions'),
    ('appraisal',      'excluded',       'bona fide third-party charge — §1026.32(b)(1) exclusions'),
    ('transfer-tax',   'excluded',       'tax — not a points-and-fees item');

CREATE TABLE IF NOT EXISTS apor_rates (
    effective_date TEXT PRIMARY KEY,   -- ISO date the APOR applies from
    apor           REAL NOT NULL
);

-- Reference APOR row (FFIEC weekly series; refresh as published).
INSERT INTO apor_rates (effective_date, apor) VALUES
    ('2025-01-01', 6.50);

CREATE TABLE IF NOT EXISTS state_high_cost_rules (
    state_code  TEXT PRIMARY KEY,
    rate_spread REAL NOT NULL,          -- APR − APOR trigger (pp)
    pf_pct      REAL NOT NULL,          -- points-and-fees % trigger
    pf_floor    REAL NOT NULL DEFAULT 0,-- loans below this amount exempt
    notes       TEXT
);

-- States with enacted high-cost statutes (trigger values are the
-- statutes' own; rows are the compliance reference, not legal advice).
INSERT INTO state_high_cost_rules (state_code, rate_spread, pf_pct, pf_floor, notes) VALUES
    ('CT', 8.0, 5.0,     0, 'CT abusive lending law'),
    ('GA', 6.5, 5.0,     0, 'GA Residential Mortgage Act high-cost loan'),
    ('IL', 6.5, 5.0,     0, 'IL High Risk Home Loan Act'),
    ('MA', 6.5, 5.0,     0, 'MA high-cost home mortgage'),
    ('MN', 6.5, 5.0,     0, 'MN high-cost lending'),
    ('NC', 6.5, 5.0,     0, 'NC high-cost home loans'),
    ('NJ', 6.5, 5.0,     0, 'NJ Home Ownership Security Act'),
    ('NM', 6.5, 5.0,     0, 'NM Home Loan Protection Act'),
    ('NY', 6.5, 5.0, 50000, 'NY high-cost home loans'),
    ('TN', 6.5, 5.0,     0, 'TN Home Loan Protection Act'),
    ('WI', 6.5, 5.0,     0, 'WI high-cost lending');

CREATE TABLE IF NOT EXISTS atr_results (
    result_id      TEXT PRIMARY KEY,
    application_id TEXT NOT NULL REFERENCES applications(id),
    eval_id        TEXT REFERENCES fee_evals(eval_id),
    dti            REAL,                -- back-end DTI %, null when uncomputable
    apr            REAL,                -- solved Appendix-J APR %, null when uncomputable
    apor           REAL,                -- APOR row applied, null when table empty
    verdicts_json  TEXT NOT NULL,       -- [{test, verdict, detail}] battery output
    actor          TEXT NOT NULL,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_atr_results_app
    ON atr_results (application_id);
