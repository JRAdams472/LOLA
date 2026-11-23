-- Per-user authorization version (MPE-275 / remediation phase 6).
-- Bumped transactionally alongside every authority-changing mutation
-- (disable, password/security reset, membership and group-role edits).
-- Stamped into login cookies and OIDC grants; a stale or missing
-- version forces re-authentication. Token rows for the subject are
-- revoked in the same transaction as the bump.
ALTER TABLE users ADD COLUMN auth_version INTEGER NOT NULL DEFAULT 1;
