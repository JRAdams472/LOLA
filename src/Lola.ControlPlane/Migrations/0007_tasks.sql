-- M16 P1 (MPE-253): officer work queue — the "next best task" surface.
-- PII-FREE BY CONSTRUCTION: rows carry refs + operational metadata
-- only (ADR-0003/0005, invariants 2/3). No doc fields, no borrower
-- attributes, no free-text detail — the ControlPlane test suite pins
-- this column set, so adding a PII-capable column fails CI.
-- Live work items stay mutable (state/claim/due): this is operational
-- state, not an audit log — claim/complete write to audit_events.
CREATE TABLE IF NOT EXISTS tasks (
    seq                 INTEGER PRIMARY KEY AUTOINCREMENT,
    task_id             TEXT NOT NULL UNIQUE,
    tenant_id           TEXT NOT NULL,
    application_id      TEXT NOT NULL,
    kind                TEXT NOT NULL,
    title               TEXT NOT NULL,          -- system-generated template, no PII
    priority            INTEGER NOT NULL DEFAULT 0,   -- higher = more urgent
    skill_tags          TEXT NOT NULL DEFAULT '[]',   -- JSON array of role/skill tags
    due_utc             TEXT,
    state               TEXT NOT NULL DEFAULT 'open'
                        CHECK (state IN ('open','claimed','done','canceled')),
    blocking_entity     TEXT,                   -- 'condition'|'step_event'|'trid_clock'|'draft_age'
    blocking_entity_id  TEXT,
    claimed_by          TEXT,
    created_utc         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now')),
    claimed_utc         TEXT,
    completed_utc       TEXT,
    updated_utc         TEXT NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%fZ','now'))
);
-- The hot path: ranked open queue per tenant.
CREATE INDEX IF NOT EXISTS ix_tasks_queue
    ON tasks (tenant_id, state, priority DESC, due_utc);
CREATE INDEX IF NOT EXISTS ix_tasks_application
    ON tasks (application_id, seq);
-- One live derived task per (app, kind, source): re-evaluation
-- upserts; resolving frees the slot for re-derivation.
CREATE UNIQUE INDEX IF NOT EXISTS ux_tasks_live_source
    ON tasks (tenant_id, application_id, kind, COALESCE(blocking_entity_id, ''))
    WHERE state IN ('open', 'claimed');
