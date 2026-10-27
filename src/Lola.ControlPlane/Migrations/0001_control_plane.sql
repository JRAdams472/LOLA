-- Control-plane schema v1: the tenant registry (ADR-0003).
-- Holds ONLY routing/provisioning metadata — never borrower PII.
-- Each row points at a per-tenant SQLite file created by M4 provisioning.
CREATE TABLE IF NOT EXISTS tenants (
    id           TEXT PRIMARY KEY,
    slug         TEXT NOT NULL UNIQUE,
    display_name TEXT NOT NULL,
    status       TEXT NOT NULL DEFAULT 'provisioning'
                 CHECK (status IN ('provisioning', 'active', 'suspended', 'retired')),
    db_path      TEXT NOT NULL,
    created_utc  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_utc  TEXT
);
CREATE INDEX IF NOT EXISTS ix_tenants_status ON tenants(status);
