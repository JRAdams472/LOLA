-- RESPA servicing/escrow posture (M23 P1, MPE-309). Reg X §1024.17
-- escrow account analysis + §1024.33 servicing-transfer notices +
-- Section 8 (§1024.15) affiliated-business inputs. Compliance posture
-- only — no payment processing, no disbursement (megaplan non-goal).
--
--   escrow_analyses         — append-only analysis rows (the read
--                             surface; verdicts also land as loan_events)
--   servicing_notices       — one row per transfer notice obligation;
--                             status transitions append notice-event
--                             rows, never mutate history
--   servicing_notice_events — append-only status lineage per notice

CREATE TABLE IF NOT EXISTS escrow_analyses (
    analysis_id           TEXT PRIMARY KEY,
    application_id        TEXT NOT NULL,
    verdict               TEXT NOT NULL CHECK (verdict IN
                          ('within','shortage','surplus','deficient','incomplete')),
    annual_disbursements  REAL,
    cushion_target        REAL,
    low_point             REAL,
    determination_amount  REAL,       -- shortage (+) or surplus (−) magnitude
    refund_required       INTEGER NOT NULL DEFAULT 0,  -- surplus ≥ $50
    inputs_json           TEXT NOT NULL,  -- engine input snapshot
    detail_json           TEXT NOT NULL,  -- projection + missing-input detail
    actor                 TEXT NOT NULL,
    created_utc           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_escrow_analyses_app
    ON escrow_analyses (application_id, created_utc);

CREATE TABLE IF NOT EXISTS servicing_notices (
    notice_id             TEXT PRIMARY KEY,
    application_id        TEXT NOT NULL,
    notice_kind           TEXT NOT NULL CHECK (notice_kind IN
                          ('transferor','transferee')),
    transfer_effective    TEXT NOT NULL,  -- ISO date of servicing transfer
    transferor_label      TEXT NOT NULL,
    transferee_label      TEXT NOT NULL,
    send_by               TEXT NOT NULL,  -- computed statutory deadline (ISO)
    status                TEXT NOT NULL CHECK (status IN
                          ('pending','sent','received')) DEFAULT 'pending',
    actor                 TEXT NOT NULL,
    created_utc           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_servicing_notices_app
    ON servicing_notices (application_id, created_utc);

CREATE TABLE IF NOT EXISTS servicing_notice_events (
    event_id              TEXT PRIMARY KEY,
    notice_id             TEXT NOT NULL,
    status                TEXT NOT NULL,
    actor                 TEXT NOT NULL,
    created_utc           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_servicing_notice_events_notice
    ON servicing_notice_events (notice_id, created_utc);
