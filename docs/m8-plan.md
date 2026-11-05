# M8 (MPE-75) plan — Fee engine adapter

The `IFeeService` seam — itemized TRID fees through a pluggable
adapter (Zara first), with an audited manual-override aggregator.
Mirrors the M7 pricing architecture wherever the contract allows.

## Vendor contract (Zara, surveyed)

- `POST /api/evaluate/{env}` (`dev|qa|uat|prod`), bearer JWT with
  `tenant_id` + `permitted_environments` claims — Zara's mgmt plane
  owns claim minting; dev path mints via `zara_devtoken` against the
  shared `.devkeys` JWKS. Prod OIDC client-credentials is a Zara-side
  follow-up — the adapter takes its token from env/file so the seam
  is unchanged when it lands.
- Request `{tenantId, scenario:{loan,borrower}}`; response
  `EvalReport` — `fees[]{feeCode, tridBucket, base, adjustments[],
  total}`, `total`, `fingerprint`. `tridBucket` maps 1:1 to LE cost
  blocks A (Origination), B (Cannot Shop), C (Can Shop), E
  (Taxes/Gov), F (Prepaids).
- Inbound PII guard rejects PII-shaped fields — same philosophy as
  MPEx. Seed tenant `acme-mortgage` (fixed UUID) carries demo rules
  (origination, transfer-tax, discount-points, prepaid-interest).

## Design calls

1. **Outbox:** `fee_evals` + `fee_outbox` mirror the M7 shape —
   same-tx enqueue, request-hash idempotency, backoff→DLQ. A
   generalized shared outbox is a bigger refactor of a shipped
   surface; deferred, not drift.
2. **Tenant mapping:** one configured `ZARA_TENANT_ID` for MVP —
   Zara tenants are fee-shop identities, not LOLA tenants; a
   per-tenant mapping table is follow-up if needed.
3. **Overrides:** `fee_items` holds the line store; an override is a
   toggle on the line (amount+reason+actor), audited `fee_override`.
   Re-eval merges by `feeCode`+`section`: overridden lines keep
   their override — recalculation never silently discards one.

## Phases

- **P1 — seam + DTOs + firewall + stub** (ADR-0007, invariant 2)
  `IFeeService`, `FeeScenario` (minimal: loan amount/purpose/type,
  purchase price/est value, zip, county, state, vaEligible,
  discountPoints), `FeeScenarioMapper` from the catalog, egress
  scan extended to the `Fees` namespace, `StubFeeService`,
  `FEE_ADAPTER` env.
- **P2 — Zara adapter + outbox + eval surface** (ADR-0007)
  `ZaraFeeAdapter` + token provider + typed errors; `0007_fees.sql`
  (`fee_evals`, `fee_outbox`, `fee_items`); `FeeOutboxWorker`;
  `POST …/fees` → 202, `GET …/fees[/history]`; `fee_eval` audit;
  **live Zara round-trip** (never-skip; CI stands up Zara's
  db+engine via public checkout, `zara_devtoken` token); second
  stub proves swap.
- **P3 — aggregator + audited overrides** (invariants 4/6)
  `fee_items` line store grouped into LE sections; `PATCH …/fees/{id}`
  override w/ required reason → `fee_override` audit; re-eval merge
  preserves overrides; `GET …/fees` returns section-grouped lines
  + effective totals.
- **P4 — marquee + gate** — submit → price → fee eval → itemized
  sections → override → re-eval preserves → audit spine; M8
  hardening gate on MPE-75.

## Risks / honest flags

- Zara prod token issuance is Zara-side future work — the adapter's
  token-file/env seam covers both paths; documented, not blocked.
- `fee_items` merge semantics must be deterministic when a fee's
  `feeCode` changes between evals (old override orphaned → item
  marked stale, never silently dropped).
- Multi-borrower freeze (MPE-139) stays a separate follow-up.
