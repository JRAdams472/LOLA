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
management. See milestone tickets under megaplan **MPE-63**.

Ops docs: [`docs/openbao-runbook.md`](docs/openbao-runbook.md) ·
[`docs/identity-runbook.md`](docs/identity-runbook.md)

## Observability

The compose stack includes **Jaeger** (traces, http://localhost:17686) and
**Seq** (structured logs, http://localhost:5441) — remapped so LOLA runs
alongside the sibling stacks. The API exports only when
the `OTEL_*` env vars are set — exporter-free otherwise. **PII never enters
logs or spans** — a redaction filter enforces this in CI.
