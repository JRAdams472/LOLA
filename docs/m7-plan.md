# M7 Implementation Plan — Pricing adapter & MPEx (MPE-71)

The PPE orchestrator: LOLA calls MPEx through an `IPricingService` seam
(ADR-0007) with a PII-free canonical scenario model. MPEx runs locally
(Rust/axum `:8080`, Dex OIDC `:5556`, service client `mpex-svc`).
MPE-91 (platform-operator identity) is folded into Phase 1.

**Decisions:** credit = two catalog fields `party[n].credit_score` +
`party[n].credit_model` (FICO/VantageScore-agnostic); lock state =
`rate_locks` table + frozen-paths enforcement; evals fire on explicit
`POST …/price`; operator = `lola-ops` role on a seeded OIDC client.

## Phase 1 — operator + seam + mapper + firewall
ADRs 0007/0008; invariants 2/5.
- `lola-ops` role + seeded confidential client (`OIDC_SEED_OPS_SECRET`);
  `PlatformOps` policy (lola-ops OR Service) on the global
  `POST/GET /api/admin/tenants` surface — TenantAdmin loses cross-tenant
  scope; tenant-scoped admin routes unchanged.
- Catalog: `party[n].credit_score` + `party[n].credit_model`
  (`fico|vantage|other`) — non-PII scenario attributes; regen
  `docs/pii-data-map.json`.
- `Application/Pricing/`: `IPricingService`, egress DTOs mirroring MPEx
  `LoanScenario`, `ScenarioMapper` (catalog-driven; `credit_score` →
  `borrower.fico`, `credit_model` → `borrower.creditModel`),
  `PricingEvaluation` normalized result.
- PII firewall: reflection CI test — egress DTOs carry no `[PiiClass]`
  members and no denylisted field names/value-shapes.
- `StubPricingAdapter` + `PRICING_ADAPTER=stub|mpex` config switch.

## Phase 2 — MPEx adapter + outbox + eval surface
ADRs 0003/0007; invariants 2/4/5.
- `MpexPricingAdapter` (Infrastructure): Dex client-credentials token
  (`MPEX_TOKEN_URL`/`_CLIENT_ID`/`_CLIENT_SECRET`/`_AUDIENCE`) w/ expiry
  cache; `POST /api/evaluate/eligibility` → `/pricing`; normalized
  inbound mapping; typed adapter errors.
- `0005_pricing.sql`: `pricing_evals` (append-only history) +
  `pricing_outbox` (kind, payload, status, attempts, next_retry, dlq).
- `PricingOutboxWorker` — hosted, registry-scoped job surface; backoff
  retry → DLQ; idempotent by request hash.
- `POST …/applications/{id}/price` → 202 + eval id; `GET …/pricing`,
  `GET …/pricing/history`; `price_eval` audit event; intake rate-limit.
- Live round-trip test vs local MPEx+Dex (env `MPEX_*`, never-skip like
  OpenBao); second stub adapter proves one-interface swap.

## Phase 3 — rate lock + freeze
Invariants 4/6.
- `0006_rate_lock.sql`: `rate_locks` (eval_id, product, rate, price,
  term, investor, locked/expires, actor, status, frozen_paths JSON).
- `LockRate` — requires a `done` eval containing product+rate+term;
  persists lock + frozen paths in one tx; `rate_lock` audit event.
- Freeze enforcement in UpdateDraft/CorrectApplication → 409 on frozen
  paths while a lock is `active`. `UnlockRate` (reason, `rate_unlock`,
  clears freeze). `GET …/lock`; `locked` on the DTO (REST+GraphQL).

## Phase 4 — asOf re-eval + marquee + gate
- `POST …/price {asOf:"<rfc3339>|draft"}` → `reval` outbox rows;
  publication-snapshot history visible in `pricing/history`.
- Marquee e2e: provision as lola-ops → submit → price → lock → frozen
  409 → unlock → asOf re-eval → audit spine.
- M7 hardening gate on MPE-71; findings → tickets.

## Env additions
`MPEX_BASE_URL`, `MPEX_TOKEN_URL`, `MPEX_CLIENT_ID`,
`MPEX_CLIENT_SECRET`, `MPEX_AUDIENCE`, `PRICING_ADAPTER`,
`OIDC_SEED_OPS_SECRET` — `.env.example` + runbook; never in repo.
