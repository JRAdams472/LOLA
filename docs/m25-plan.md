# M25 — AUS adapters (DU + LPA) — phase plan

Parent: MPE-303. Megaplan line 45, conforms ADR-0007 (AUS rides `IVendorAdapter`); invariants 2 (PII-free egress where vendor contract allows — note: AUS submissions are inherently PII-bearing to the GSE, see below), 3 (zero PII in logs/audit), 5 (TLS + OIDC client-credentials).

## Key design decision — the egress posture

DU/LPA submissions are **PII-bearing by nature** — unlike MPEx (PII-free pricing), the GSEs underwrite the actual borrower: name, SSN, income, assets are the input. This is not drift: invariant 2's egress contract is *per-vendor* — `VendorEgress` allowlists exist precisely to declare which field classes each vendor may receive. The AUS vendors get the widest allowlist in the system, and that width must be (a) explicit in the allowlist declaration, (b) enforced by the reflection firewall in CI, (c) disclosed in the ticket proof. Credentials resolve from OpenBao KV per tenant — never shared fallback (existing `OpenBaoVendorCredentialSource`).

## What exists already

`VendorKind.Aus` in the enum, `IVendorAdapter` seam (SubmitAsync/VendorReceipt), vendor order rows + completion channels, `VendorEgress` machinery, OpenBao credential source, conditions machinery (M21), HMDA LAR assembler with AUS columns NA'd pending this milestone (M24).

## Phase 1 — AUS domain: MISMO wire + normalized results (MPE-TBD)

Schema migration `0022_m25_aus.sql`:
- `aus_results` — append-only: application_id, vendor (du/lpa), order correlation, recommendation, risk class, key/case id, raw response hash, created. Raw response XML is PII-bearing → stored inside the tenant boundary (same as LAR rows), never in events/logs.
- `aus_findings` — append-only normalized findings: result_id, code, category, severity, message, condition linkage nullable.

Domain (`src/Lola.Domain/Aus/`):
- `MismoAusSerializer` — application doc → MISMO 3.4 AUS request XML. XSD-validated against published MISMO schemas (fixture-verified, freely available).
- `DuResponseParser` / `LpaResponseParser` — DU findings XML / LPA feedback certificate XML → normalized `AusResult` + findings. Fixtures derived from Fannie's publicly published DU Specification Test Case Suite + Freddie's published ULDD/feedback formats — wire-shape proof without credentials.
- `AusNormalizer` — vendor-specific findings → canonical finding model (code/category/severity); recommendation mapping (Approve/Eligible, Accept, Refer, Caution, etc.).

Tests: serializer XSD-valid; both parsers round-trip published fixtures; normalizer maps every fixture finding; no PII in normalized finding model beyond what conditions already carry.

## Phase 2 — Adapters + wiring + HMDA linkage (MPE-TBD)

- `DuAdapter` / `LpaAdapter` implementing `IVendorAdapter` — submit serialized MISMO over TLS w/ OIDC client-credentials, correlate receipts, parse responses. No live credentials exist: adapters are contract-tested against recorded fixtures; live certification is the external M27 program per the parent ticket.
- `VendorEgress` allowlists declared per vendor (widest-in-system, explicit); reflection-firewall CI guard extended if needed so an undeclared PII-class member fails the build.
- Handlers: `SubmitAus` (vendor param, append-only result + findings rows + hash-chained `aus_result` event — counts/ids only), `GetApplicationAus` (posture read: latest per vendor + lineage).
- Findings → conditions auto-population: severity-driven mapping opens `conditions` rows deduped by finding code (mirrors M23 overdue-notice pattern); manual close-out remains officer action.
- HMDA linkage: `LarAssembler` AUS columns populate from the latest result per vendor (system name + result) — completing the M24 export. Backward-compatible: no results → NA as today.
- Endpoints: `POST …/aus/submit?vendor=`, `GET …/aus`. Staff-scoped double layer.

Tests: negative matrix — undeclared PII member fails reflection guard; non-staff 403; findings dedupe; HMDA columns NA→populated transition; event PII hygiene proven.

## Phase 3 — Officer surface + e2e (MPE-TBD)

- GraphQL `aus` resolver (staff-null).
- `AusPanel` staff-only workspace tab: per-vendor latest recommendation, findings list with severity, submit affordance (vendor picker), lineage. Findings-linked conditions cross-reference.
- `lib/aus.ts` hooks; jest suite; Playwright e2e against live stack with fake/stub adapter wired in the docker compose: submit → result + findings render → conditions auto-opened → re-submit lineage → HMDA panel shows AUS populated.
- Screenshots attached.

## Proof plan + gate

Each acceptance bullet maps to run output; e2e proves the lifecycle; milestone closes with the hardening gate (OWASP + ADR drift + parent code-shape on MPE-303). Live GSE certification is explicitly out of scope — external program, M27 close-out per the parent ticket.
