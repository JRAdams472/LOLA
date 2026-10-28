-- Per-tenant migration rollout tracking (MPE-93). DbUp's journal
-- inside each tenant file stays authoritative for applied state; this
-- table is the operator-facing rollout status per tenant per script.
CREATE TABLE IF NOT EXISTS tenant_migrations (
    tenant_id   TEXT NOT NULL REFERENCES tenants(id),
    version     INTEGER NOT NULL,
    name        TEXT NOT NULL,
    applied_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    status      TEXT NOT NULL CHECK (status IN ('applied', 'failed')),
    error       TEXT,
    PRIMARY KEY (tenant_id, version, name)
);
