-- Vendor service-step outcomes (M13 P2, MPE-227): vendor orders
-- report 'ordered'/'completed'/'failed'/'referred' events against
-- the service steps (Credit|Voe|Voi|Flood|Fraud|Aus) — the 0003
-- CHECK only admits the linear pipeline's entered/keyed. SQLite
-- can't alter a CHECK, so the table is rebuilt with the widened
-- vocabulary (same rebuild shape as 0009_fee_variants).
CREATE TABLE application_step_events_v2 (
    id             TEXT PRIMARY KEY,
    application_id TEXT NOT NULL REFERENCES applications(id),
    step           TEXT NOT NULL,
    event          TEXT NOT NULL
                   CHECK (event IN ('entered','keyed','ordered',
                                    'completed','failed','referred')),
    actor          TEXT NOT NULL,
    reason         TEXT,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
INSERT INTO application_step_events_v2
    (id, application_id, step, event, actor, reason, created_utc)
SELECT id, application_id, step, event, actor, reason, created_utc
FROM application_step_events;
DROP TABLE application_step_events;
ALTER TABLE application_step_events_v2
    RENAME TO application_step_events;
CREATE INDEX IF NOT EXISTS ix_step_events_app
    ON application_step_events (application_id, created_utc);
