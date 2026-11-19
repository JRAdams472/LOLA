# LOLA M16 — Task engine + Next-Best-Task queue

Parent: MPE-244 under megaplan MPE-83. Depends on M15 (done).

The operating layer: derive work from loan state in the tenant file,
project **PII-free** task rows into the control-plane queue, and serve
officers a unified ranked "next best task" surface.

**Conforms to:** ADR-0002 (DbUp append-only), ADR-0003 (control plane
holds cross-tenant operational state, tenant files hold PII),
ADR-0005 (control-plane placement), ADR-0007 (seams unchanged),
ADR-0011 (masked-by-default — queue rows carry refs, never values).
Invariants 2 (PII-free egress — queue rows are an egress boundary) and
3 (zero PII in logs/metadata).

## Shape

- `tasks` lives in the **control plane** (cross-tenant officer worklist).
  Columns carry refs + metadata only: task_id, tenant_id,
  application_id, kind, priority, skill_tags, due_utc, state
  (open/claimed/done/canceled), blocking refs (condition_id,
  linked_entity/id), claimed_by, created_utc, claimed_utc,
  completed_utc, title (system-generated, no PII). **No doc fields, no
  borrower attributes, no free-text detail.**
- `TaskEvaluator` (Application layer) reads tenant-side state —
  open/blocking conditions, `application_step_events`, TRID clock,
  draft age — and upserts PII-free `tasks` rows. Triggered on the
  condition/pipeline mutation paths **and** lazily at queue read, so
  time-driven tasks (TRID proximity, stale drafts) stay current
  without a background worker.
- Queue read: ranked `priority desc, due_utc asc` with optional
  skill/role filter; claim/complete are audited transitions that
  remove the task from the open queue.

## Phases

| Phase | Ticket | Delivers |
|---|---|---|
| P1 — schema + domain + port | MPE-253 | `0007_tasks.sql`, `Task`/`TaskDraft`/`TaskKind`/`TaskState`, `ITaskRepository` + `TaskQueue` control-plane impl, migration test, PII-column scan test |
| P2 — evaluator | MPE-254 | `TaskEvaluator` deriving tasks from open blocking conditions + step events + TRID + stale drafts; wired into condition open/transition and pipeline paths; on-read refresh; dedupe by (app, kind, ref) |
| P3 — queue surface | MPE-255 | `GET …/tasks` (ranked, skill filter, staff), `POST …/tasks/{id}/claim`, `POST …/tasks/{id}/complete` (audited, state-guarded), `GET …/tasks/next`; GraphQL `tasks` + `nextTask` (staff-only, borrower null) |
| P4 — console Queue + gate | MPE-256 | officer console Queue page (ranked list, do-next navigation, per-officer filter), live marquee, OWASP/ADR-drift gate on MPE-244, code-shape, close-out |

## Acceptance map

- Open blocking condition → queue task for matching skills — P2 + P3 test
- Claim/complete audited, completed leaves queue — P3 test
- UI shows highest-priority first + do-next flow — P4 marquee
- Zero PII in `tasks` — P1 column scan + P2 row inspection test
