# M22 — ATR/QM + high-cost engine (MPE-300)

Compliance megaplan milestone M22. Computes the statutory rate-and-fee
determinations — ATR/QM (§1026.43), HOEPA high-cost (§1026.32), Section 35
HPML (§1026.35), HUD QM (24 CFR 203.19), and per-state high-cost rules —
over application-document fields already inside the cipher boundary and
the M21 fee substrate (`fee_evals`/`fee_items` + classification
registries). Results are pass/fail/warn verdicts written as attributed,
hash-chained `loan_events` plus an append-only `atr_results` row for the
read surface — snapshots stay the source of truth.

Rides existing machinery — **no architecture drift**:

- Inputs are app-doc paths (`loan_amount`, `interest_rate`,
  `loan_term_months`, `amortization_type`, `loan.product_type`,
  `loan.lien_position`, `subject_property.state`, income + liability
  arrays) and `fee_items` — all inside the tenant file (ADR-0003).
- Outputs are `loan_events` + `conditions`-adjacent rows per ADR-0006 —
  the document/events surface, not a new store.
- Pure-domain calculators in `Lola.Domain/Compliance/` mirror
  `TridTolerance`/`TridClock` shape; persistence via Dapper per ADR-0002.
- Reads ride GraphQL, mutations REST-via-BFF (ADR-0011), staff-scoped
  (`LolaPolicies.ScopeFor`), masked-by-default (ADR-0008).

## Statutory inputs → sources

| test input | source |
|---|---|
| loan amount / note rate / term / amortization | `loan.base_amount`/`loan_amount`, `interest_rate`, `loan_term_months`, `amortization_type` |
| monthly income | `party[i].employment[j].monthly_income` (summed) |
| monthly debts | `liability[i].monthly_payment` (summed) |
| proposed payment | computed — amortization math over note terms |
| points & fees | `fee_items` of the latest completed eval + `pf_classes` registry (fail-closed to *counted*) |
| APOR | `apor_rates` — per-tenant table, seeded; each result records the row used |
| state rule | `state_high_cost_rules` keyed by `subject_property.state` |

Missing required input → **warn** (`needs_review`), never a silent pass —
mirrors M21's fail-closed classification.

## Phases

### Phase 1 — schema + domain engine (MPE-30x)

Migration `0019_m22_atr_qm.sql` (additive, no rebuilds):

- `pf_classes(fee_code, category, notes)` — points-and-fees registry:
  `counted` | `excluded` (+ `discount_point` sub-tag for the §1026.32(b)(1)(i)
  bona-fide discount exclusion). Seed the shipped Zara codes;
  unseeded codes classify `counted`.
- `apor_rates(effective_date, apor)` — seeded with a current reference
  APOR; the result DTO echoes the effective row used.
- `state_high_cost_rules(state_code, rate_spread, pf_pct, pf_floor, notes)`
  — per-state triggers seeded for the statute-bearing states.
- `atr_results(result_id, application_id, eval_id, dti, apr, apor,
  verdicts_json, actor, created_utc)` — append-only; rows never update.

Domain (`src/Lola.Domain/Compliance/`):

- `LoanMath.cs` — monthly P&I payment (fixed, interest-only,
  graduated→amortizing), and the APR solver (Reg Z Appendix J — iterate
  the payment-stream rate; prepaid finance charges from `pf_classes`).
- `AtrQm.cs` — the test battery returning `AtrQmResult(test, verdict,
  detail)` rows:
  - `atr_general` — eight-factor reasonability from the mapped inputs
    (income, debts, payment): warn on missing, pass when computed DTI
    is reasonable under §1026.43(c) factor presence.
  - `qm_general` — §1026.43(e): term ≤ 360, no IO/neg-am/balloon
    features, points-and-fees ≤ cap, DTI ≤ 43%.
  - `qm_hud` — 24 CFR 203.19 FHA QM: P&F ≤ 3% (scaled under the
    small-loan breakpoint); only evaluates when `product_type='fha'`.
  - `hoepa` — §1026.32: APR−APOR spread (6.5pp first lien / 8.5pp
    subordinate-or-small) or P&F > 5% (8% under the small-loan floor).
  - `hpml` — §1026.35: APR−APOR > 1.5pp first-lien conforming,
    > 2.5pp jumbo, > 3.5pp subordinate.
  - `state_high_cost` — applies the seeded row for the property state;
    no row → `pass` with detail "no state rule".

Domain tests fixture-verify every threshold edge: APR solver against
known rate/fee schedules, DTI boundary (43.00 vs 43.01), each HOEPA/HPML
tier boundary, P&F cap edges, missing-input warns, unknown fee code →
counted.

### Phase 2 — evaluation wiring + endpoints (MPE-30x)

- `EvaluateAtrQm` handler — resolves the app doc, latest completed fee
  eval + items, `pf_classes`, applicable APOR row (as-of now or supplied
  effective date), and state rule; runs `AtrQm.Run`; inserts the
  `atr_results` row + `atr_qm_result` `loan_event` (verdict summary
  only — no field values) in the same repository transaction family as
  M21. Staff actor.
- `GetApplicationAtr` — latest result + test verdicts + result lineage.
- Endpoints: `GET /applications/{id}/atr` (staff-scoped read),
  `POST /applications/{id}/atr/evaluate` (explicit re-run — results are
  append-only, so re-evaluation is a new row, not an update).
- `LoanEventKind.AtrQmResult = "atr_qm_result"`.
- Application tests: missing-fee-eval → warn battery, wrong-state →
  right table row, event written with actor, staff-scope enforcement,
  double-evaluate → two rows.

### Phase 3 — officer surface + e2e (MPE-30x)

- `atr` GraphQL resolver field — same handler, staff-scope → null
  (ADR-0011).
- `AtrQmPanel` on the loan workspace — per-test verdict badges
  (pass/warn/fail) with DTI/APR/P&F figures and the APOR row used;
  "Evaluate" affordance for staff.
- Jest (DTO mapping, badge render, missing-data warn) + Playwright e2e:
  seed → set loan terms → evaluate → see verdicts.

## Milestone hardening gate (before MPE-300 closes)

OWASP + app hardening + ADR-drift pass over the merged surface per
`AGENTS.md` — findings comment on MPE-300, then code-shape, then Done.

Conforms ADR-0002/0003/0006/0011; invariants 1, 4.
