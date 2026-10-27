-- Control-plane schema v2: identity, groups/roles, visibility, invites,
-- OpenIddict stores (MPE-67 / ADR-0005). Auth identifiers only — no PII
-- beyond email-as-username; masking applies in responses, not at rest.

-- SQLCipher file key wrapped by the tenant transit key (`vault:` blob).
ALTER TABLE tenants ADD COLUMN db_key_wrapped TEXT;

CREATE TABLE IF NOT EXISTS users (
    id              TEXT PRIMARY KEY,
    tenant_id       TEXT NOT NULL REFERENCES tenants(id),
    email           TEXT NOT NULL,
    display_name    TEXT NOT NULL,
    password_hash   TEXT,               -- Argon2id PHC string; NULL = not yet set
    status          TEXT NOT NULL DEFAULT 'invited'
                    CHECK (status IN ('invited', 'active', 'disabled')),
    mfa_enabled     INTEGER NOT NULL DEFAULT 0,
    mfa_secret_ref  TEXT,               -- MFA-ready; populated by the future MFA ticket
    created_utc     TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now')),
    updated_utc     TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_users_tenant_email ON users(tenant_id, email);

CREATE TABLE IF NOT EXISTS groups (
    id          TEXT PRIMARY KEY,
    tenant_id   TEXT NOT NULL REFERENCES tenants(id),
    name        TEXT NOT NULL,
    description TEXT
);
CREATE UNIQUE INDEX IF NOT EXISTS ux_groups_tenant_name ON groups(tenant_id, name);

CREATE TABLE IF NOT EXISTS group_roles (
    group_id TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    role     TEXT NOT NULL CHECK (role IN ('TenantAdmin', 'LoanOfficer', 'Borrower', 'Service')),
    PRIMARY KEY (group_id, role)
);

CREATE TABLE IF NOT EXISTS user_groups (
    user_id  TEXT NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    group_id TEXT NOT NULL REFERENCES groups(id) ON DELETE CASCADE,
    PRIMARY KEY (user_id, group_id)
);

-- Platform-wide role → field-class visibility (ADR-0008). 'masked' is
-- the default everywhere; a row only exists where a role is granted
-- 'plaintext'. Effective access = union over the user's group roles.
CREATE TABLE IF NOT EXISTS role_visibility (
    role      TEXT NOT NULL CHECK (role IN ('TenantAdmin', 'LoanOfficer', 'Borrower', 'Service')),
    pii_class TEXT NOT NULL CHECK (pii_class IN ('Name', 'Address', 'Contact', 'Ssn', 'Dob', 'Account', 'Income', 'Other')),
    access    TEXT NOT NULL DEFAULT 'plaintext' CHECK (access IN ('masked', 'plaintext')),
    PRIMARY KEY (role, pii_class)
);

-- Seed: absence of a row means masked. TenantAdmin sees all plaintext;
-- LoanOfficer gets working-field plaintext (sensitive classes stay
-- masked — audited reveal arrives in M6); Borrower and Service get
-- nothing (masked-by-default, ADR-0008).
INSERT INTO role_visibility (role, pii_class, access)
SELECT 'TenantAdmin', c, 'plaintext' FROM (
    SELECT 'Name' AS c UNION ALL SELECT 'Address' UNION ALL SELECT 'Contact'
    UNION ALL SELECT 'Ssn' UNION ALL SELECT 'Dob' UNION ALL SELECT 'Account'
    UNION ALL SELECT 'Income' UNION ALL SELECT 'Other')
UNION ALL
SELECT 'LoanOfficer', c, 'plaintext' FROM (
    SELECT 'Name' AS c UNION ALL SELECT 'Address' UNION ALL SELECT 'Contact'
    UNION ALL SELECT 'Income' UNION ALL SELECT 'Other');

-- Single-use invite tokens: only the SHA-256 of the token is stored —
-- the link itself is a secret and must not be recoverable from the DB.
CREATE TABLE IF NOT EXISTS invites (
    id           TEXT PRIMARY KEY,
    tenant_id    TEXT NOT NULL REFERENCES tenants(id),
    email        TEXT NOT NULL,
    role         TEXT NOT NULL DEFAULT 'Borrower'
                 CHECK (role IN ('TenantAdmin', 'LoanOfficer', 'Borrower', 'Service')),
    token_hash   TEXT NOT NULL UNIQUE,
    expires_utc  TEXT NOT NULL,
    used_utc     TEXT,
    created_by   TEXT NOT NULL,
    created_utc  TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);

-- OpenIddict store tables (custom Dapper stores, ADR-0005). Columns
-- mirror the fields the IOpenIddict*Store contracts require.
CREATE TABLE IF NOT EXISTS openiddict_applications (
    id                          TEXT PRIMARY KEY,
    client_id                   TEXT NOT NULL UNIQUE,
    client_secret               TEXT,          -- hashed by OpenIddict, never plaintext
    client_type                 TEXT,
    consent_type                TEXT,
    display_name                TEXT,
    display_names               TEXT,          -- JSON
    redirect_uris               TEXT,          -- JSON array
    post_logout_redirect_uris   TEXT,          -- JSON array
    permissions                 TEXT,          -- JSON array
    requirements                TEXT,          -- JSON array
    settings                    TEXT,          -- JSON object
    properties                  TEXT,          -- JSON object
    concurrency_token           TEXT NOT NULL DEFAULT (lower(hex(randomblob(16))))
);

CREATE TABLE IF NOT EXISTS openiddict_authorizations (
    id                TEXT PRIMARY KEY,
    application_id    TEXT REFERENCES openiddict_applications(id) ON DELETE CASCADE,
    creation_date     TEXT,
    properties        TEXT,
    scopes            TEXT,
    status            TEXT,
    subject           TEXT,
    type              TEXT,
    concurrency_token TEXT NOT NULL DEFAULT (lower(hex(randomblob(16))))
);
CREATE INDEX IF NOT EXISTS ix_oidc_auth_subject ON openiddict_authorizations(subject);
CREATE INDEX IF NOT EXISTS ix_oidc_auth_app ON openiddict_authorizations(application_id);

CREATE TABLE IF NOT EXISTS openiddict_scopes (
    id                TEXT PRIMARY KEY,
    name              TEXT NOT NULL UNIQUE,
    description       TEXT,
    descriptions      TEXT,
    display_name      TEXT,
    display_names     TEXT,
    properties        TEXT,
    resources         TEXT,
    concurrency_token TEXT NOT NULL DEFAULT (lower(hex(randomblob(16))))
);

CREATE TABLE IF NOT EXISTS openiddict_tokens (
    id                TEXT PRIMARY KEY,
    application_id    TEXT REFERENCES openiddict_applications(id) ON DELETE CASCADE,
    authorization_id  TEXT REFERENCES openiddict_authorizations(id) ON DELETE CASCADE,
    creation_date     TEXT,
    expiration_date   TEXT,
    payload           TEXT,
    properties        TEXT,
    redemption_date   TEXT,
    reference_id      TEXT UNIQUE,
    status            TEXT,
    subject           TEXT,
    type              TEXT,
    concurrency_token TEXT NOT NULL DEFAULT (lower(hex(randomblob(16))))
);
CREATE INDEX IF NOT EXISTS ix_oidc_tokens_subject ON openiddict_tokens(subject);
CREATE INDEX IF NOT EXISTS ix_oidc_tokens_app ON openiddict_tokens(application_id);
