# M24 — HMDA plan (MPE-302)

**Compliance reporting posture** — LOLA assembles the HMDA Loan
Application Register (LAR) from data already inside the tenant cipher
boundary: application doc fields, M22 ATR results (rate spread, HOEPA
status), and workflow state (action taken). Per the megaplan: LAR export
is a PII-bearing artifact — generated inside the tenant boundary, masked-
by-default UI, export writes a hash-chained audit event (invariants 1/2/4).

## Gap analysis (catalog vs. CFPB Filing Instruction Guide)

Present today (~20 consumable fields): demographics.ethnicity/race/sex +
collection_method, loan_amount, loan_purpose, interest_rate,
loan_term_months, amortization_type, loan.cltv/ltv/lien_position/
product_type, subject_property.{street,city,state,zip,county,est_value,
units,usage}, party[0].{dob,credit_score,credit_model}, escrow.*,
declaration.*, M22-computed APR/APOR (rate spread derivable), HOEPA
verdict, DTI — but none of the computed values are *persisted* as
reportable fields.

Missing (~25 reportable fields): census_tract (per-loan), action_taken +
date, occupancy conformance, construction_method, manufactured-home
secured/land fields, multifamily_affordable_units, submission_of_application,
initially_payable_to, reverse_mortgage, open_end_line_of_credit,
business_or_commercial_purpose, interest_only_payment, balloon_payment,
negative_amortization / other non-amortizing features, prepayment_penalty_term,
origination_charges, discount_points, lender_credits, intro_rate_period,
income relied upon, co-applicant demographics presence flags, ethnicity/
race sub-fields (up to 5). Institution-level: LEI + NMLS id (tenant-level
configuration, not per-loan doc fields).

## Shape (mirrors M21/M22/M23 — schema/domain → wiring/endpoints → surface/e2e)

### Phase 1 — schema + domain engines

- Migration `0021_m24_hmda.sql` (tenant file, DbUp append-only):
  - `lar_rows` — append-only assembled LAR rows: application_id,
    reporting_year, row_json (the ~110-column record as JSON), uli,
    actor, created_utc. Never updated — re-assembly inserts.
  - `edit_check_runs` — append-only: lar_row_id, tier, edit_id,
    result (pass/fail/warn), detail, created_utc.
  - `lar_snapshots` — reporting-year freeze: year, row count, artifact
    reference, frozen_by/created_utc; `lar_snapshot_rows` mapping.
- Catalog additions in `mismo-field-map.json` (+ regenerate client
  field-maps): the ~25 missing `hmda.*` / `subject_property.census_tract`
  fields above (non-core, PII class none/Other — no new PII classes;
  note demographics already carry their classes). Tenant-level
  `lei`/`nmls_id` settings on the control-plane tenant row (settings
  JSON column already exists — verify; additive key only).
- `src/Lola.Domain/Hmda/Uli.cs` — ULI generation: LEI + 23-char
  loan-unique suffix + ISO 7064 mod-97-10 check digit; fixture-proven
  against CFPB published examples.
- `src/Lola.Domain/Hmda/LarAssembler.cs` — doc + latest atr_results +
  workflow state → the 110-column LAR record. Derivations: rate spread
  (APR − APOR for lien position), HOEPA status, DTI, age (from dob),
  action taken/date from application status history, income (hmda
  override field else employment sum). Missing/NA handling per FIG —
  explicit NA codes, never blank-invention.
- `src/Lola.Domain/Hmda/EditChecks.cs` — tier framework:
  - **Syntactical** — pipe-count/length, data-type, valid-value enums.
  - **Validity** — field-level CFPB edits (V-series, representative
    battery: required-field presence, valid ranges, ULI check digit).
  - **Quality** — cross-field plausibility (Q-series battery: CLTV vs
    amount/value consistency, rate vs. spread sanity, income/DTI
    plausibility, action-taken/date coherence).
  - Each edit carries its CFPB edit id; uncovered edits are explicitly
    enumerated in the engine docs — the framework is tier-complete,
    the battery is the core set (full ~250-edit coverage is beyond
    scope and is named as a close-out item).
- Domain tests: ULI check-digit vectors, assembler derivations
  (rate spread sign, NA codes, age math), each edit proven both sides
  of its boundary.

### Phase 2 — wiring + endpoints

- `HmdaPorts.cs` — `IHmdaRepository` + records/DTOs (double for REAL
  columns per the M22 lesson).
- `AssembleLar` — doc + ATR + state → engine → ONE append-only
  `lar_rows` row + edit-check run rows + ONE `hmda_lar` loan event
  (counts only, invariant 3 — no field values in events).
- `GetApplicationHmda` — latest LAR row + edit results grouped by tier
  + lineage; non-staff 403.
- `ExportLar` — reporting-year pipe-delimited artifact (TS row + LAR
  rows per FIG format) generated inside the tenant boundary + ONE
  hash-chained audit event (actor, year, row count — invariant 2
  explicit). Snapshot freeze: `FreezeLarSnapshot` appends the year
  snapshot rows; re-freeze appends, never rewrites.
- Endpoints: `GET …/hmda`, `POST …/hmda/assemble`,
  `GET …/hmda/export?year=` (file download), `POST …/hmda/freeze`.
  Staff-scoped at endpoint AND handler layers.
- Application tests mirroring M23: assemble persist/readback, edit-run
  append-only lineage, export byte-shape + audit attribution, freeze
  idempotent-dedupe, non-staff 403, event PII hygiene.

### Phase 3 — officer surface + e2e

- GraphQL `hmda` resolver (staff-null per ADR-0011).
- `HmdaPanel` staff-only tab — completeness/edit-check results grouped
  by tier with CFPB edit ids, key LAR figures, Assemble affordance,
  export download + snapshot freeze, append-only lineage note.
- jest component tests + Playwright e2e against the live stack: seed →
  assemble → edit checks render → export downloads pipe-delimited →
  audit event → re-assemble lineage.

### Milestone hardening gate

OWASP + app-hardening + ADR-drift pass on the merged surface; findings
comment on MPE-302 before close (per AGENTS.md).

## Conformance

- **ADR-0002/0003** — LAR rows/edits/snapshots in the tenant file via
  DbUp append-only migration; no new data-plane technology.
- **ADR-0006** — HMDA fields live in the document payload/catalog;
  assembled rows are read surfaces derived from the doc, not parallel
  truth.
- **Invariants**: 1 (export generated inside tenant boundary — never
  egresses as plaintext outside the artifact download, which is itself
  an attributed staff action), 2/4 (export + freeze write hash-chained
  audit events), 3 (events carry counts/ids only), 6 (LAR rows append-
  only; re-assembly inserts), 8 (demographics inherit existing masked-
  by-default visibility — no new reveal path).
- **M25 dependency note**: AUS name/result LAR columns assemble as NA
  until M25 lands — tolerated by design, documented in the engine.
