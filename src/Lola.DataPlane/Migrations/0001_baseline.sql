-- Tenant data-plane baseline (MPE-93). All tenant-file schema lives in
-- migrations — the provisioner creates only the keyed file.
CREATE TABLE IF NOT EXISTS schema_info (
    version     INTEGER NOT NULL DEFAULT 1,
    created_utc TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ', 'now'))
);
INSERT INTO schema_info (version)
SELECT 1 WHERE NOT EXISTS (SELECT 1 FROM schema_info);
