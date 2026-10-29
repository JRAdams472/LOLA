-- Tenant data-plane workflow step events (M5 P4, ADR-0007 seam).
-- Append-only: every pipeline step entry/attempt is recorded per
-- application with actor and reason; rows are never updated or deleted.
CREATE TABLE IF NOT EXISTS application_step_events (
    id             TEXT PRIMARY KEY,
    application_id TEXT NOT NULL REFERENCES applications(id),
    step           TEXT NOT NULL,
    event          TEXT NOT NULL
                   CHECK (event IN ('entered','keyed')),
    actor          TEXT NOT NULL,
    reason         TEXT,
    created_utc    TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
CREATE INDEX IF NOT EXISTS ix_step_events_app
    ON application_step_events (application_id, created_utc);
