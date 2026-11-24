-- Server-side BFF session custody (MPE-289 / remediation phase 9):
-- the browser holds an opaque handle; token material lives only here,
-- transit-encrypted under the control-plane domain. Handle lookups are
-- SHA-256 hashes — a leaked row grants nothing without the handle.
CREATE TABLE IF NOT EXISTS bff_sessions (
    namespace      TEXT    NOT NULL,
    handle_hash    TEXT    NOT NULL,
    subject        TEXT    NOT NULL,
    tenant_id      TEXT    NOT NULL,
    auth_version   INTEGER NOT NULL,
    at_ct          TEXT    NOT NULL,
    rt_ct          TEXT,
    name_ct        TEXT,
    at_exp_ms      INTEGER NOT NULL,
    rt_exp_ms      INTEGER,
    last_active_ms INTEGER NOT NULL,
    abs_exp_ms     INTEGER NOT NULL,
    idle_ms        INTEGER,
    rev            INTEGER NOT NULL DEFAULT 0,
    created_ms     INTEGER NOT NULL,
    revoked_at_ms  INTEGER,
    PRIMARY KEY (namespace, handle_hash)
);
