# M23 — RESPA servicing/escrow plan (MPE-301)

**Compliance posture only** — LOLA is not a servicing platform. No
payment processing, no disbursement: those are megaplan non-goals.
What lands: the statutory *records* a lender must produce and the
deadline tracking around servicing transfers.

## Shape (mirrors M21/M22 — schema/domain → wiring/endpoints → surface/e2e)

### Phase 1 — schema + domain engine (MPE-30x)

- Migration `0020_m23_respa.sql` (tenant file, DbUp append-only):
  - `escrow_analyses` — append-only analysis rows: projected
    disbursements, cushion target, shortage/surplus determination,
    input figures snapshot JSON, actor, created_utc. Never updated.
  - `servicing_notices` — one row per transfer event: effective date,
    transferor/transferee labels, computed statutory deadlines,
    notice_kind (transferor/transferee), sent/received timestamps —
    status transitions append new `servicing_notice_events` rows
    rather than mutating.
  - `servicing_notice_events` — append-only status history.
- Catalog additions in `mismo-field-map.json` (+ regenerate
  `clients/web/lib/field-map.ts` via `scripts/gen-field-map.mjs`):
  - `escrow.current_balance`, `escrow.monthly_payment`,
    `escrow.annual_property_tax`, `escrow.annual_hazard_insurance`,
    `escrow.annual_pmi`, `escrow.annual_hoa` (decimal, non-core,
    PII class none/Other)
  - `settlement_provider[n].service_type` / `.provider_name` /
    `.affiliated_with_lender` — Section 8 AfBA inputs
- `src/Lola.Domain/Compliance/EscrowAnalysis.cs` — §1024.17 math:
  12-month disbursement projection from annual figures; cushion
  target capped at 2 months (1/6 of annual disbursements — the
  statutory maximum); trial-balance low point vs. cushion →
  shortage / surplus / deficiency / within determination; surplus
  ≥ $50 flags the 30-day refund clock. Missing inputs → incomplete
  verdict, never silent pass.
- `src/Lola.Domain/Compliance/ServicingTransfer.cs` — §1024.33
  deadlines: transferor notice ≥ 15 days before effective date;
  transferee notice ≤ 15 days after; overdue computation.
- Domain tests: fixture-verified cushion cap, low-point math,
  shortage/surplus boundaries ($50 refund threshold both sides),
  deadline windows both sides of the statutory boundary.

### Phase 2 — wiring + endpoints (MPE-30x)

- `RespaPorts.cs` — `IRespaRepository` + records/DTOs (double for
  REAL columns per the M22 lesson).
- `RunEscrowAnalysis` — doc inputs → engine → ONE append-only
  `escrow_analyses` row + ONE `escrow_analysis` loan event (verdict
  summary only, invariant 3).
- `RecordServicingTransfer` / `MarkNoticeSent` / `MarkNoticeReceived`
  — staff actions; every transition writes a hash-chained loan event
  + append-only notice-event row.
- `GetApplicationRespa` — posture read: latest analysis + lineage,
  AfBA flag (any `settlement_provider[n].affiliated_with_lender` —
  surfaces the Section 8 disclosure requirement), notices with
  computed deadlines + live overdue evaluation. **Overdue opens a
  `servicing_notice` condition** (new ConditionKind) once — deduped.
- Endpoints: `GET …/respa`, `POST …/respa/escrow/analyze`,
  `POST …/respa/servicing/transfer`, `POST …/respa/servicing/notices/{id}/sent`,
  `POST …/notices/{id}/received` — staff-scoped, handlers re-check.
- `LoanEventKind` additions: `escrow_analysis`, `servicing_notice`.
- Application tests — negative matrix: non-staff 403, missing inputs
  → incomplete never 500, append-only re-runs, overdue dedupe,
  no-disbursement guard (no endpoint writes payments, ever).

### Phase 3 — officer surface + e2e (MPE-30x)

- `respa` GraphQL resolver — same staff-scoped handler, null for
  non-staff.
- `RespaPanel` on the loan workspace (staff-only RESPA tab): escrow
  analysis card (verdict + figures + refund-clock flag), AfBA
  disclosure banner when triggered, servicing notices table with
  deadline countdowns + sent/received affordances.
- Escrow/AfBA input fields land in the officer workspace catalog
  sections (the generated field-map regenerates).
- Jest: DTO mapping, verdict badges, AfBA banner, overdue rendering,
  role gating. Playwright e2e: seed escrow inputs → analyze → verdict;
  record transfer → deadline computed → mark sent/received;
  force-overdue transfer → condition surfaces.

## Milestone hardening gate (before MPE-301 closes)

OWASP + app hardening + ADR-drift pass per `AGENTS.md` — findings
comment on MPE-301, then parent code-shape, then Done.

Conforms ADR-0003 (all rows in the tenant file) / ADR-0006 (posture
surfaces ride the document/events model — no new egress) / ADR-0011
(reads via GraphQL over the same handlers); invariants 4 (every write
attributed, hash-chained) / 6 (analysis + notice records append-only).
