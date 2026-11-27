-- HMDA reporting posture (M24 P1, MPE-312). LAR rows are a reporting
-- projection assembled from the application document + ATR results +
-- workflow state — never a parallel store of truth. Reg C (12 CFR
-- 1003) pipe-delimited export per the CFPB Filing Instruction Guide.
--
--   lar_rows           — append-only assembled LAR rows (the read
--                        surface); re-assembly inserts, never updates
--   edit_check_runs    — append-only per-row edit results, one row per
--                        (lar_row, edit) evaluation; CFPB edit id + tier
--   lar_snapshots      — reporting-year freeze headers (append-only)
--   lar_snapshot_rows  — which lar_rows a freeze covers (append-only)

CREATE TABLE IF NOT EXISTS lar_rows (
    lar_row_id            TEXT PRIMARY KEY,
    application_id        TEXT NOT NULL,
    reporting_year        INTEGER NOT NULL,
    uli                   TEXT NOT NULL,  -- LEI + suffix + ISO 7064 check digit
    row_json              TEXT NOT NULL,  -- the assembled ~110-column LAR record
    derived_json          TEXT NOT NULL,  -- derivation provenance (rate spread
                                          -- source eval id, atr row id, etc.)
    actor                 TEXT NOT NULL,
    created_utc           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_lar_rows_app
    ON lar_rows (application_id, created_utc);
CREATE INDEX IF NOT EXISTS ix_lar_rows_year
    ON lar_rows (reporting_year, created_utc);

CREATE TABLE IF NOT EXISTS edit_check_runs (
    run_id                TEXT PRIMARY KEY,
    lar_row_id            TEXT NOT NULL REFERENCES lar_rows (lar_row_id),
    tier                  TEXT NOT NULL CHECK (tier IN
                          ('syntactical','validity','quality')),
    edit_id               TEXT NOT NULL,  -- CFPB edit identifier (e.g. V608)
    result                TEXT NOT NULL CHECK (result IN ('pass','fail','warn')),
    detail                TEXT NOT NULL DEFAULT '',
    created_utc           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_edit_check_runs_row
    ON edit_check_runs (lar_row_id, tier);

CREATE TABLE IF NOT EXISTS lar_snapshots (
    snapshot_id           TEXT PRIMARY KEY,
    reporting_year        INTEGER NOT NULL,
    row_count             INTEGER NOT NULL,
    actor                 TEXT NOT NULL,
    created_utc           TEXT NOT NULL
);
CREATE INDEX IF NOT EXISTS ix_lar_snapshots_year
    ON lar_snapshots (reporting_year, created_utc);

-- A freeze covers exactly the rows listed here. Because both tables are
-- append-only, a re-freeze creates a NEW snapshot row covering whatever
-- rows exist at that time — the original freeze never changes shape.
CREATE TABLE IF NOT EXISTS lar_snapshot_rows (
    snapshot_id           TEXT NOT NULL REFERENCES lar_snapshots (snapshot_id),
    lar_row_id            TEXT NOT NULL REFERENCES lar_rows (lar_row_id),
    PRIMARY KEY (snapshot_id, lar_row_id)
);
