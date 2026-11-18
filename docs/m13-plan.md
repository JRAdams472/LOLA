# M13 (MPE-77) — Vendor adapter seam plan

Build the third-party verification hub behind the locked ADR-0007
adapter-seam: `IVendorAdapter` contracts for credit (tri-merge),
VOE/VOI, flood certification, fraud/OFAC, and AUS (DU/LP); stub
providers returning realistic normalized responses; vendor credentials
in OpenBao KV; an async order lifecycle (request → webhook/poll →
normalized result → application record); vendor orders surfaced as
named pipeline steps on the M5 orchestration seam.

## Context & decisions confirmed

Milestone **M13 / MPE-77** — depends on M5 (orchestration seam), merged.
ADR-0007 names `IVendorAdapter` explicitly, so this is conformance, not
new architecture. Decisions:

- **One port, five kinds** — `IVendorAdapter` is the DTS-style swap
  point: `Kind` (credit|voe|voi|flood|fraud|aus) ×
  `SubmitAsync(order) → VendorReceipt` and `PollAsync(receipt) →
  VendorResult?`. Per-kind request/result DTOs are normalized (a
  `CreditReport` looks the same whether it came from a stub or a real
  bureau) so vendor swap-in touches the adapter only. Adding a vendor =
  one new class implementing the interface — the acceptance bullet.
- **Egress is minimum-fields-per-kind, not PII-free** — credit/fraud
  legitimately need SSN/name/DOB; flood needs the property address;
  AUS needs the full 1003. The invariant-2 firewall is extended with a
  per-kind field allowlist (reflection test asserts each request DTO
  carries only its declared fields) — the ISPPr
  "minimal-template-fields" pattern, not the MPEx blanket-free one.
- **Results are PII — encrypted at rest** — a tri-merge credit report
  is some of the most sensitive data in the system. `vendor_orders
  .result_json` stores `vault:` ciphertext via `IFieldCipher`;
  reads unmask through the same masking pipeline (ADR-0008); every
  result read writes an audit row (invariant 4).
- **Creds in OpenBao KV, literally** — the ticket says KV, so a thin
  `OpenBaoKvClient` (sibling to `OpenBaoTransitClient`) reads/writes
  `lola-kv/vendors/{tenant}/{kind}`; an `IVendorCredentialSource`
  port resolves them at call time. Nothing in config, DB rows, or
  repo. (ISPPr's wrapped-key-in-registry stays as-is — its amendment
  predates this ticket's KV wording; no migration needed.)
- **Async lifecycle reuses the proven outbox shape** — `vendor_outbox`
  + registry-scoped `VendorOutboxProcessor`/`VendorOutboxWorker` with
  bounded attempts, backoff, DLQ — the same pattern as
  `fee_outbox`/`doc_outbox`, now generalized enough that the three
  share a shape worth noting (no refactor this milestone).
- **Two completion channels** — real vendors differ: some push
  webhooks, some need polling. Both are first-class:
  `POST /api/vendor-webhooks/{kind}` (REST ingress per ADR-0011 —
  the codebase's first webhook endpoint: HMAC signature check using
  the KV-held secret, replay dedupe by `request_hash`, rate-limited)
  and `PollAsync` in the drain for vendors without push. The stub
  exercises both so a real vendor can land on either channel.
- **Named pipeline steps** — `WorkflowPipeline.Steps` gains
  `Credit`, `Voe`, `Flood`, `Fraud`, `Aus` as service steps. They do
  NOT join the linear `Order` chain (vendor checks are orthogonal,
  orderable in any sequence): the pipeline gains a service-step
  concept — ordering an adapter records
  `application_step_events(step=<kind>, outcome=ordered)` and
  completion records `completed|failed`, so the officer Steps tab
  shows vendor activity without changing submit→validate flow.
  This is a design judgment call flagged for review — the
  alternative (five serial pipeline positions) would force an
  artificial ordering the ticket doesn't ask for.
- **Internal-only surface** — vendor orders are officer-side. REST +
  GraphQL read resolvers deny `ActorIsBorrower` outright (the M12
  lockdown pattern); `PortalLoanDto` contract tests extended to
  assert no vendor fields leak to the portal.
- **Stub determinism** — `VENDOR_ADAPTER=stub` (compose default)
  returns seeded-but-realistic fixtures: credit tri-merge with three
  bureau tradelines, VOE with employer/tenure, flood zone
  determination, OFAC clear-with-score, AUS approve/eligible
  findings. Deterministic per `request_hash` so e2e is stable.

## Conforming ADRs

| Phase concern | ADR |
|---|---|
| `IVendorAdapter` seam, OIDC creds, vendor swap | 0007 (+ MPE-72 cred amendment) |
| `vendor_orders`/`vendor_outbox` DbUp migrations, Dapper | 0002 |
| Per-tenant tables, no cross-tenant path | 0003 |
| Creds in OpenBao KV; results `vault:` ciphertext | 0004 |
| Webhook = REST; officer reads = GraphQL | 0011 |
| Masked reads on vendor results | 0008 |
| Zero PII in logs/traces — vendor payloads never logged | 0009 |
| One PR per phase, 80% coverage floor | 0010 |

## Phases

### Phase 1 — contracts, schema, stubs
`IVendorAdapter` + normalized per-kind DTOs; migration
`0012_vendor.sql` (`vendor_orders`, `vendor_outbox`); `VendorRepository`;
`StubVendorAdapter` behind `VENDOR_ADAPTER`; `OpenBaoKvClient` +
`IVendorCredentialSource`; per-kind egress allowlist + reflection
firewall extension. Acceptance groundwork: stub submit returns a
normalized receipt.
- `src/Lola.Application/Vendors/{Contracts.cs,VendorDtos.cs,VendorPorts.cs,VendorGuards.cs}`
- `src/Lola.Infrastructure/Vendors/{StubVendorAdapter.cs,VendorOptions.cs}`
- `src/Lola.Infrastructure/OpenBao/OpenBaoKvClient.cs`
- `src/Lola.DataPlane/VendorRepository.cs` + migration

### Phase 2 — order lifecycle
`POST /api/tenants/{t}/applications/{id}/vendor-orders` (staff-only;
borrower 403); `PlaceVendorOrder` handler → validate → outbox + order
row; `VendorOutboxProcessor`/worker with retry/DLQ; completion writes
encrypted `result_json` + `application_step_events`; service-step
registration in `WorkflowPipeline`.
- `src/Lola.Application/Vendors/{PlaceVendorOrder.cs,GetVendorOrder.cs,ListVendorOrders.cs}`
- `src/Lola.Api/Vendors/{VendorEndpoints.cs,VendorOutboxProcessor.cs,VendorOutboxWorker.cs}`

### Phase 3 — completion channels
`POST /api/vendor-webhooks/{kind}` — HMAC verify (KV secret), replay
dedupe, bounded body, rate limit; poll path in drain; idempotent
result application (completed orders never reprocessed); stub
webhook simulator for e2e.
- `src/Lola.Api/Vendors/VendorWebhookEndpoints.cs`
- `src/Lola.Application/Vendors/CompleteVendorOrder.cs`

### Phase 4 — read surface + console
GraphQL `vendorOrders(applicationId)` officer resolver (borrower →
null); officer console Steps tab lists vendor orders with status
chips + normalized summary; audit on place/complete/read; portal
contract test asserts no vendor fields.
- `src/Lola.Api/GraphQL` resolver additions
- `clients/web` Steps tab vendor-order rows

### Phase 5 — marquee e2e, hardening gate, close-out
Marquee: officer orders all five stub services on one application →
outbox → webhook + poll completions → results on record → Steps tab →
borrower still sees nothing. Hardening gate on MPE-77 (webhook auth,
replay, KV cred proof, egress minimization, PII-in-logs sweep, dep
freshness, ADR drift). README/wiki/code-shape/follow-ups.

## Proof-of-completion map

| Acceptance bullet | Proof |
|---|---|
| Stub vendor order completes full lifecycle into application record | e2e: order → outbox → result_json + step event; marquee screenshots |
| Adding a vendor = one interface | reflection test: adapter registry resolves by `VENDOR_ADAPTER`; stub is the proof impl |
| Creds never in config/DB/repo | KV-only source; gitleaks clean; test asserts no cred path touches registry/config |
| Hardening gate | findings comment on MPE-77 before close |
