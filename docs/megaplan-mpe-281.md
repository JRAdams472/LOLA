# LOLA Phase-3 Megaplan — MPE-281 (Compliance & Integrations)

Compliance gap inventory from MPE-281, promoted to a delivery plan.
Builds entirely on shipped machinery — **no architecture drift**:
TRID tolerance extends `TridClock` + the fee engine; ATR/QM, HOEPA,
RESPA, HMDA are pure tenant-side domain engines; DU/LPA, credit, and
AVM ride the ADR-0007 `IVendorAdapter` seam (M13) — no new outbound
plumbing, no core changes.

Regulatory posture: LOLA computes and **evidences** compliance posture
(warnings, blocks, audit trails, exports) — the lender of record owns
the legal determination. Every engine emits hash-chained events, never
silent flags.

## Locked design calls

1. **Tolerance buckets are fee-version diffs, not a new document type.**
   LE↔CD versions are already immutable fee snapshots; tolerance
   tracking classifies each fee into the 0% / 10% / unlimited bucket
   and diffs consecutive versions — reuses the append-only snapshot
   machinery (invariant 6), no parallel ledger.
2. **Compliance engines live in `Lola.Domain`** as pure calculators
   over the MISMO document + fee snapshots (same shape as `TridClock`).
   Orchestration (when to run, where results land) is Application-side;
   no engine touches I/O.
3. **DU/LPA and credit bureaus are `VendorKind.Aus`/`VendorKind.Credit`
   adapters** — the M13 seam already carries PII to vendors under
   per-kind egress allowlists. AVM is the only additive seam change:
   a new `VendorKind.Avm` + order/result records (enum extension, not
   an ADR amendment).
4. **HMDA LAR is a reporting projection** — collected fields ride the
   application document's `hmda` catalog paths; the pipe-delimited LAR
   export is generated output (like MISMO/ULDD exports), not a second
   store.
5. **Six milestones M21–M26 + gate M27**, mirroring the M15–M20 tail.

## Milestones

| # | Deliverable | Ticket | Deps |
|---|---|---|---|
| M21 | TRID tolerance engine — fee→bucket classification (0% / 10% / unlimited); LE↔CD version diffing; changed-circumstance reset tracking; tolerance-violation events + cure workflow hooks (conditions/task integration); officer surface for variance status | MPE-TBD | M20 |
| M22 | ATR/QM + high-cost engine — ATR exemption/QM tests, HOEPA points-and-fees triggers, Section 35 thresholds, state high-cost rules (threshold table per state), HUD QM rate-and-fee tests; pass/fail/warn result events | MPE-TBD | M21 |
| M23 | RESPA servicing/escrow — escrow account analysis records, anti-kickback (Section 8) affiliated-business disclosure flags, servicing-transfer notice tracking | MPE-TBD | M21 |
| M24 | HMDA — LAR field catalog paths + collection UI/validation, edit checks (syntactical/validity/quality tiers), pipe-delimited LAR export, reporting-year snapshots | MPE-TBD | M21 |
| M25 | AUS adapters — DU + LPA submission/response implementations behind `VendorKind.Aus`; per-vendor `VendorEgress` allowlists; findings→conditions auto-population; contract fixtures | MPE-TBD | M21 |
| M26 | Credit tri-merge + AVM — bureau adapter behind `VendorKind.Credit` (tri-merge merge rules, VantageScore 4.0 / FICO 10T model tagging); `VendorKind.Avm` seam extension + adapter (value/confidence normalization into the application doc) | MPE-TBD | M25 |
| M27 | Compliance hardening, UAT & close-out — threat-model delta (regulatory data surface + new egress kinds), UAT matrix extension, wiki/runbook refresh, program gate, close-out tickets | MPE-TBD | M21–M26 |

## Per-milestone ADR/invariant mapping

- **M21** — ADR-0002/0003/0006; invariants 4 (every reset/cure is a
  hash-chained event with actor + reason), 6 (tolerance diffs read
  append-only fee snapshots — never mutate them), 8 (variance UI
  inherits role visibility — no new reveal path)
- **M22** — ADR-0006 (results land in the document/events surface);
  invariants 1/4 (test inputs are doc fields already inside the
  cipher boundary; results are attributed events)
- **M23** — ADR-0003 (escrow/servicing rows in the tenant file);
  invariants 4/6 (servicing records append-only)
- **M24** — ADR-0006 (HMDA fields in the document payload + index
  columns); invariants 1/2 (LAR export is a PII-bearing artifact —
  generated inside the tenant boundary, masked-by-default UI, export
  writes an audit event)
- **M25** — ADR-0007 (AUS rides `IVendorAdapter`); invariants 2/3/5
  (per-kind egress allowlist enforced by the reflection firewall;
  creds from OpenBao KV; OIDC+TLS; findings normalized PII-free into
  conditions)
- **M26** — ADR-0007; invariants 1/2 (bureau requests legitimately
  carry SSN/DOB — declared on the allowlist exactly; tri-merge merge
  output stored as PII ciphertext inside the tenant boundary)
- **M27** — all ADRs, all invariants; program-level gate

## Non-goals

- **Live vendor credentials / DU-LPA onboarding programs** — code
  delivers contract-tested adapters + fixtures; actual GSE/bureau
  enrollment is an external program (tracked as a close-out ticket,
  same as MPEx/ISPPr onboarding).
- e-signature (ISPPr-side, MPE-239).
- Investor delivery / securitization beyond MISMO+ULDD exports.
- New data-plane technology — Dapper/DbUp/SQLite-per-tenant locked
  (ADR-0002/0003).
- RESPA servicing *operations* (payment processing, escrow
  disbursement) — LOLA tracks compliance posture, it is not a
  servicing platform.

## Hardening gate

M27 closes with the program-level gate (OWASP + app hardening + ADR
drift) per `AGENTS.md`; every milestone carries its own gate comment
before close.
