# M15 (MPE-243) plan — Mutation event log + conditions domain

Phase-2 foundation (megaplan `docs/megaplan-mpe-83.md`): an append-only
mutation log recording every field change, override, escalation, and
lifecycle transition — plus the `conditions` entity M16–M18 write to.

## Design calls

1. **`loan_events` mirrors `audit_events`** — same `seq`/`event_id`/
   `prev_hash`/`hash` spine and the existing `HashChain` mechanics,
   extended with mutation payload columns: `application_id`,
   `event_kind` (field_change|override|escalation|transition|reveal|
   condition_*), `field_path`, `field_class`, `before_val`,
   `after_val`. PII-class fields store **blind-index HMAC** of the
   value (deterministic tenant-keyed hash — lets the log show
   "changed vs. unchanged," never plaintext, never a usable oracle
   without the tenant key); non-PII fields store plaintext.
   Snapshots stay the source of truth — events are additive
   (invariant 6 untouched).
2. **Emission joins the existing write transaction** — draft PATCH
   already runs `BEGIN IMMEDIATE` in `PatchDraftAsync`; the event rows
   are written on the same connection inside it (capture the before-doc
   in-tx, diff per patched path). Other mutation paths (fee override,
   transition, reveal) follow the same pattern — one atomic unit per
   mutation, never a fire-and-forget event.
3. **`conditions` is a plain tenant entity** — `condition_id`,
   `application_id`, `kind` (doc_required|data_validation|
   distance_check|manual), `state` (open|closed|waived), `blocking`,
   `linked_entity`/`linked_entity_id`, `title`, `detail`,
   `opened_by` (actor or `auto:<source>`), timestamps, close/waive
   reason. Waive requires reason + audit event; state transitions are
   guarded (closed→open only via reopen; waived is terminal).
4. **Staff-scoped reads** — `GET /api/tenants/{t}/applications/{id}/
   events|conditions`, GraphQL `loanEvents`/`conditions` fields, chain
   `verify` endpoint — all `TenantStaff` policy; borrowers get a
   borrower-shaped conditions view only (their own app, no internal
   detail) — aligns with M19's external audiences later.
5. **ADR conformance** — 0002 (Dapper/DbUp `0014_events_conditions.sql`),
   0003 (tenant file), 0006 (additive events, snapshots unchanged).
   Invariants 1 (hashed PII), 4 (hash-chained append-only), 6.

## Phases

- **P1 — schema + domain + writer port** — migration
  `0014_events_conditions.sql`; `LoanEvent`/`Condition` domain types;
  `ILoanEventLog` + `IConditionRepository` ports; DataPlane repos;
  hash-chain reuse + `EventChain` verify; unit tests (hash coverage,
  chain break detection).
- **P2 — emission on mutation paths** — `LoanEventWriter`; draft PATCH
  diff emission inside `PatchDraftAsync`'s tx; fee-override,
  transition, reveal emitters; blind-index hashing of PII values;
  regression tests proving same-tx atomicity + hashed vs plaintext
  column behavior.
- **P3 — conditions lifecycle + read surface** — conditions CRUD +
  state guards + waive-reason+audit; REST endpoints; GraphQL fields;
  chain `verify` endpoint; borrower-scoped conditions view.
- **P4 — marquee e2e + gate + close-out** — live walk: patch→events,
  waive→audit, verify chain, snapshot immutability check; README/wiki;
  hardening gate on MPE-243.
