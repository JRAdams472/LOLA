-- Blind-index columns for control-plane identity fields (MPE-276 /
-- Phase 7b, design docs/control-plane-encryption-design.md).
-- Keyed, normalized HMAC indexes will carry equality lookups and
-- uniqueness once field encryption lands (Phase 7c); the plaintext
-- UNIQUE constraints stay until the offline conversion (Phase 7d)
-- backfills these columns. New columns start NULL — SQLite UNIQUE
-- indexes treat NULLs as distinct, so no constraint fires early.

ALTER TABLE users   ADD COLUMN email_idx TEXT;
ALTER TABLE invites ADD COLUMN email_idx TEXT;
ALTER TABLE tenants ADD COLUMN slug_idx  TEXT;
ALTER TABLE groups  ADD COLUMN name_idx  TEXT;
ALTER TABLE openiddict_applications ADD COLUMN client_id_idx TEXT;

-- Tenant-scoped email uniqueness on the keyed index (replaces
-- ux_users_tenant_email once populated).
CREATE UNIQUE INDEX IF NOT EXISTS ux_users_tenant_email_idx
    ON users(tenant_id, email_idx);

CREATE UNIQUE INDEX IF NOT EXISTS ux_tenants_slug_idx
    ON tenants(slug_idx);

CREATE UNIQUE INDEX IF NOT EXISTS ux_groups_tenant_name_idx
    ON groups(tenant_id, name_idx);

-- svc-{tenant} client ids embed the tenant id — the lookup index moves
-- to the keyed blind index.
CREATE UNIQUE INDEX IF NOT EXISTS ux_oidc_apps_client_id_idx
    ON openiddict_applications(client_id_idx);

-- Invite lookup by email within a tenant — lookup index, not a
-- uniqueness constraint (multiple invite rows per email are legal).
CREATE INDEX IF NOT EXISTS ix_invites_tenant_email_idx
    ON invites(tenant_id, email_idx);
