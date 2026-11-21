-- TPO link table (M19 / MPE-265): which external partners may reach a
-- given application inside this tenant (ADR-0003 — the link lives in
-- the tenant file, never the control plane). `revoked_utc` is the soft
-- delete: an unlinked partner loses scope without losing the link's
-- provenance; loan_events + audit carry the history.
-- `partner_user_id` names a control-plane user — cross-DB reference is
-- impossible in SQLite, so membership in a Broker/Realtor group is
-- enforced by the link handler, not a FK.
CREATE TABLE IF NOT EXISTS application_partners (
    app_id          TEXT NOT NULL REFERENCES applications(id),
    partner_user_id TEXT NOT NULL,
    partner_role    TEXT NOT NULL CHECK (partner_role IN ('Broker', 'Realtor')),
    linked_by       TEXT NOT NULL,
    linked_utc      TEXT NOT NULL,
    revoked_utc     TEXT,
    PRIMARY KEY (app_id, partner_user_id)
);
-- The hot paths: "is this partner linked to this app" (row-scope guard,
-- PK lookup) and "which apps does this partner see" (scoped list).
CREATE INDEX IF NOT EXISTS ix_application_partners_partner
    ON application_partners (partner_user_id)
    WHERE revoked_utc IS NULL;
