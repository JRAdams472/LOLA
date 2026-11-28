# M26 plan — Credit tri-merge + AVM (MPE-304)

Per the megaplan row: bureau adapter behind `VendorKind.Credit` (tri-merge
merge rules, VantageScore 4.0 / FICO 10T model tagging); `VendorKind.Avm`
seam extension + adapter (value/confidence normalization into the
application document). Conforms ADR-0007; invariants 1, 2.

## Substrate (verified before planning)

- `VendorKind.Credit` + egress allowlist (SSN/DOB/name/address) + request
  record + mapper + reflection firewall already exist and are CI-enforced —
  the M26 egress acceptance bullet is largely pre-proven; the milestone
  adds the *processing* side, not the egress side.
- `VendorKind.Avm` does NOT exist — enum extension, allowlist, request
  record, mapper paths all greenfield.
- The stub already emits tri-merge-shaped credit data (bureaus array with
  score/scoreModel/tradelines) — the canonical completion shape precedent
  from M25 applies.
- No tri-merge logic exists anywhere — greenfield domain work.
- Doc writes on submitted applications append NEW versions, never UPDATE
  (`CorrectApplication`, invariant 6); catalog paths are validated.
  AVM normalization rides this pattern with a system actor.

## Design decisions (surfaced, not silent)

1. **AVM egress is address-only.** An automated valuation model needs the
   subject property address + type characteristics — no borrower identity.
   The `VendorKind.Avm` allowlist declares exactly the property fields;
   the reflection firewall proves nothing else can ride along. This is the
   narrowest allowlist after Flood.
2. **Vendor-completion doc writes go through a dedicated system path.**
   AVM results normalize into catalog paths (`avm.*`) by appending a NEW
   doc version attributed to the vendor-completion system actor — never
   an UPDATE (invariant 6), audit-attributed (invariant 4), field-validated
   like any other write. RateFreeze enforcement applies.
3. **Merged credit report is ciphertext at rest.** The merged output is
   PII-bearing (tradelines, scores, identifiers) — stored inside the
   tenant-file `credit_reports` table (SQLCipher + per-tenant DEK at the
   file layer), raw bureau responses confined likewise. The officer UI is
   masked-by-default per invariant 8.
4. **Xactus is the base integration interface** (user-confirmed
   2026-11-28). The MISMO v2.3.1 credit-response shape our parsers
   target matches Xactus's public API documentation — the adapter
   in Phase 2 writes against that spec. **Bureau wire formats are not public artifacts.** Unlike MISMO, the
   tri-merge bureau formats (Equifax/TU/Experian) are licensee-only.
   Fixtures will be synthetic-but-shaped on published integration-guide
   excerpts with the same provenance documentation as M25 — contract
   tests prove OUR parse/merge logic, not vendor certification. Same
   disclosure as DU CodifiedFindings in M25.
5. **Tri-merge merge rules are explicit and test-pinned:** tradeline
   dedupe by (creditor, masked-account) across bureau files; conflict
   resolution most-recent-reporting wins for balance/status date,
   worst-status wins for delinquency; score selection mid-of-three /
   lower-of-two per agency convention; score-model tagging preserved
   per bureau file — never averaged across models.
6. **Liabilities reconcile into the loan doc (user-directed scope).**
   Bureau tradelines match borrower-entered `liability[i]` entries by
   explicit rules — exact masked-account match first, then normalized
   creditor name + monthly-payment tolerance. Matched liabilities get
   balance + payment UPDATED to the bureau-reported values; unmatched
   tradelines are APPENDED as new `liability[n]` entries marked
   `liability[i].source = tri_merge` so officers can distinguish
   bureau-discovered debt from borrower-declared. Writes ride the same
   vendor-completion system write-path as AVM — new append-only version,
   never UPDATE (invariant 6). Discovered debts correctly flow into DTI
   for ATR — that is the point of the feature.
7. **Delinquency activity is structured, not buried.** 30/60/90-day
   late counts within a 24-month lookback extract per tradeline and
   aggregate across the merged report — stored as queryable columns on
   `credit_reports` AND normalized into the doc (`credit.lates_30`,
   `credit.lates_60`, `credit.lates_90`) so pricing consumes them
   through the existing scenario-mapper path. Late counts are credit
   attributes, not identity data — consistent with `credit_score`
   already flowing to MPEx PII-free; the pricing firewall declaration
   is extended exactly.

## Phases

### Phase 1 — tri-merge domain + AVM model (MPE-318)
- Migration `0023_m26_credit_avm.sql`: `credit_reports` (merged output,
  per-report bureau lineage) + `avm_results`, append-only, tenant-file.
- `TriMergeMerger` domain engine: dedupe/conflict/score-selection rules
  above, pure functions over parsed bureau files.
- Bureau response parsers (per-bureau shapes, typed failure on malformed
  like M25) + AVM response parser.
- Catalog +4 fields: `avm.value`, `avm.confidence`, `avm.model`,
  `avm.valuation_date` — client field-map regen.
- Fixture harvest (subagent-parallel again — worked well in M25):
  bureau-format public excerpts, AVM public spec material, synthetic
  tri-merge files with conflicting tradelines for merge-rule proof.
- Domain tests: every merge rule firing + passing, score-model tagging
  preserved, malformed XML typed failure, parser coverage.
- Conforms ADR-0007; invariants 1/2 design evidence.

### Phase 2 — adapters + completion wiring (MPE-319)
- `VendorKind.Avm` seam extension end-to-end: enum, egress allowlist
  (property-only), `AvmOrderRequest`, mapper paths, stub report shape,
  reflection-guard proof.
- `CreditBureauAdapter` + `AvmAdapter` behind `IVendorAdapter`,
  contract-tested against fixtures; OpenBao KV per-tenant credentials.
- `ProcessCreditCompletion` — parse per-bureau files → TriMergeMerger →
  ONE append-only merged `credit_reports` row; findings/scores PII-free
  in events (counts only, invariant 3).
- `ProcessAvmCompletion` — normalize value/confidence → append doc
  version via the system write-path; `avm_results` row for lineage.
- Endpoints: `POST …/credit/order`, `GET …/credit`, `POST …/avm/order`,
  `GET …/avm` — staff-scoped double layer. SQL-layer repository tests.
- Application tests: merge-on-completion, dedupe, doc-write versioning,
  negative cases.

### Phase 3 — officer surface + e2e (MPE-320)
- GraphQL `credit` + `avm` resolvers — staff-null pattern.
- `CreditPanel`: per-bureau scores with model tags, merged tradelines
  with bureau-of-origin + merge status, masked-by-default identifiers
  (invariant 8), order affordance. `AvmPanel` or merged into a valuation
  card: value + confidence + model + date, order affordance.
- jest + Playwright e2e against live stack with canonical stub shapes:
  order → merged report renders → tradeline merge visible → AVM value
  lands in the application doc (cross-check via workspace field) →
  re-order lineage.
- Screenshots on the ticket.

Then the milestone hardening gate per AGENTS.md.

## Risks / honest caveats

- Bureau fixture shapes are synthetic — documented like M25; live bureau
  certification is the same external program track as DU/LPA.
- Mid-score selection rules vary by investor — the engine implements the
  agency-conventional default; investor-specific overrides are out of
  scope (configuration surface if ever needed).
