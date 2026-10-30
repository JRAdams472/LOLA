# LOLA

**LOLA** is a multi-tenant **loan origination system**: the PII-bearing front
end of the mortgage pipeline. It collects MISMO-aligned loan applications
from borrowers and loan officers, stores all personal data encrypted in
physically isolated per-tenant SQLite vaults, and hands **PII-free** loan
models downstream to MPEx for pricing/eligibility and minimal-payload
document requests to ISPPr for disclosure generation.

This system stores protected personal data. Every layer — transit, rest,
egress, logs, and deletion — is built around that fact. See the security
invariants in [`AGENTS.md`](AGENTS.md) and the megaplan in
[`docs/megaplan-mpe-63.md`](docs/megaplan-mpe-63.md).

## Stack

| Layer | Technology |
|---|---|
| API | C# / .NET 10, ASP.NET Core |
| Data access | Dapper + `Microsoft.Data.Sqlite` (`SQLitePCLRaw` SQLCipher bundle); DbUp append-only migrations — **no EF Core** |
| Data plane | SQLite — one SQLCipher-encrypted file per tenant |
| Control plane | SQLite (tenant registry, identities, OIDC clients) |
| Secrets / keys | OpenBao transit (envelope encryption, per-tenant DEKs) + KV |
| Identity | OpenIddict (OIDC; custom Dapper-backed stores) |
| Object store | MinIO (borrower document vault) + ClamAV (upload scanning) |
| Web | Next.js (App Router) + TypeScript — officer console (MUI) + borrower portal |
| Integrations | MPEx (PII-free eval egress), ISPPr (document packages) |
| Dev/deploy | Docker Compose; OTel → Seq + Jaeger |

## Status

Control plane live — OIDC login (PKCE + client credentials), tenant
provisioning with per-tenant SQLCipher files, role→visibility masking
on every response, borrower invites, and admin tenant/user/group
management.

Data plane live (M4) — DbUp rolling migrations inside keyed tenant
files with fleet rollout + per-tenant tracking, a context-scoped
repository seam, encrypted `VACUUM INTO` backups with sha256 manifests
and guarded restore, and key-first crypto-shredding erasure
(`DELETE /api/admin/tenants/{id}`).

Intake live (M5) — a MISMO-aligned field catalog drives everything:
JSON1 application documents, encrypted PII leaves (`vault:`) with
blind-index (`idx:`) search, catalog-driven validation, and the
hand-rolled MISMO XML import/export + ULDD export mappers.
REST `/api/tenants/{id}/applications` and GraphQL share the same
handlers — draft → submit → in_review → decisioned, officer-any /
borrower-own scoping, masked-by-default reads, operator corrections
as append-only versions, a once-only TRID clock with business-day
deadline, and the `POSIntake → Validate → PPE → Fee → Doc → Package`
named-step workflow seam. See milestone tickets under
megaplan **MPE-63**.

Data hygiene live (M6) — append-only hash-chained audit in every
tenant file plus the control plane (actor, entity, PII classes, reason —
never values), tamper-evident `…/audit/verify`, audited officer reveal
(`POST …/{id}/reveal {reason}`) inside masked-by-default reads, access
review reports, a generated `docs/pii-data-map.json` with a CI drift
test, a 25-month Reg B retention gate on erasure (`?force&reason`
writes `retention_override` to the surviving control-plane log), and a
post-erasure residual lifecycle that deletes invites/OIDC credentials/
groups and anonymizes users while retaining the audit spine.

Pricing live (M7) — `PRICING_ADAPTER=mpex` selects the MPEx adapter:
Dex `client_credentials` with an expiry-cached token, eligibility →
pricing calls normalized into a vendor-neutral `PricingEvaluation`,
and a reflection-guarded PII firewall proving the egress DTOs carry
no personal data. Evals run through a per-tenant `pricing_outbox`
(same-tx enqueue, backoff → DLQ, request-hash idempotency) drained
by a registry-scoped worker; `POST …/price` → 202 + eval id,
`GET …/pricing[/history]` reads the append-only spine, `asOf`
re-evals resolve historic publication snapshots. `POST …/lock`
converts a completed eval into a rate lock that freezes the
eval-determining catalog paths (patch → 409 with `frozenFields`)
until an audited `POST …/unlock`; `ApplicationDto.Lock` carries
status + expiry on REST and GraphQL. The `lola-ops` platform
operator owns the global tenant registry surface.

Ops docs: [`docs/openbao-runbook.md`](docs/openbao-runbook.md) ·
[`docs/identity-runbook.md`](docs/identity-runbook.md) ·
[`docs/data-plane-runbook.md`](docs/data-plane-runbook.md)

## Observability

The compose stack includes **Jaeger** (traces, http://localhost:17686) and
**Seq** (structured logs, http://localhost:5441) — remapped so LOLA runs
alongside the sibling stacks. The API exports only when
the `OTEL_*` env vars are set — exporter-free otherwise. **PII never enters
logs or spans** — a redaction filter enforces this in CI.
