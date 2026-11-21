-- TPO sub-tenancy identity (M19 / MPE-265): partner roles Broker and
-- Realtor enter the fixed role set (ADR-0005). `users.org_name` carries
-- the TPO organization attribution; `invites.org_name` ferries it from
-- the admin's invite to the accepting user.
--
-- SQLite cannot alter CHECK constraints, so `invites` and `group_roles`
-- are rebuilt. `role_visibility` is deliberately NOT widened: no row
-- can ever name a partner role, so partner roles are masked at the
-- storage layer — not just by convention (ADR-0008, ticket acceptance).

ALTER TABLE users ADD COLUMN org_name TEXT;

CREATE TABLE invites_v2 (
    id           TEXT PRIMARY KEY,
    tenant_id    TEXT NOT NULL REFERENCES tenants(id),
    email        TEXT NOT NULL,
    role         TEXT NOT NULL DEFAULT 'Borrower'
                 CHECK (role IN ('TenantAdmin', 'LoanOfficer', 'Borrower',
                                 'Service', 'Broker', 'Realtor')),
    org_name     TEXT,
    token_hash   TEXT NOT NULL UNIQUE,
    expires_utc  TEXT NOT NULL,
    used_utc     TEXT,
    created_by   TEXT NOT NULL,
    created_utc  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);
INSERT INTO invites_v2
    (id, tenant_id, email, role, org_name, token_hash, expires_utc,
     used_utc, created_by, created_utc)
SELECT id, tenant_id, email, role, NULL, token_hash, expires_utc,
       used_utc, created_by, created_utc
FROM invites;
DROP TABLE invites;
ALTER TABLE invites_v2 RENAME TO invites;

CREATE TABLE group_roles_v2 (
    group_id TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    role     TEXT NOT NULL CHECK (role IN ('TenantAdmin', 'LoanOfficer',
                                           'Borrower', 'Service',
                                           'Broker', 'Realtor')),
    PRIMARY KEY (group_id, role)
);
INSERT INTO group_roles_v2 (group_id, role)
SELECT group_id, role FROM group_roles;
DROP TABLE group_roles;
ALTER TABLE group_roles_v2 RENAME TO group_roles;
