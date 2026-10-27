# LOLA Megaplan — MPE-63

Multi-tenant, MISMO-aligned **loan origination system** — the PII-bearing
front end of the mortgage pipeline. C#/.NET 10 API, Dapper + DbUp, SQLite
file-per-tenant data plane (SQLCipher + per-tenant OpenBao DEKs + field-level
transit encryption on PII-class columns), SQLite control plane, OpenIddict
identity, Next.js officer console + borrower portal, secured integrations to
MPEx (PII-free egress) and ISPPr (document packages), MinIO + ClamAV
document vault, hash-chained audit, crypto-shredding retention.

## Architecture (decided)

| Layer | Choice |
|---|---|
| API | ASP.NET Core (.NET 10 LTS) |
| Data access | **Dapper** + `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlcipher`; **DbUp** append-only `migrations/NNNN_name.sql` — no EF Core |
| Data plane | SQLite — one encrypted file per tenant; relational schema for indexed/regulated fields + JSON1 payload columns for canonical application snapshots |
| Control plane | SQLite (tenant registry, users, OIDC clients) |
| Keys/secrets | **OpenBao** transit (KEK → per-tenant DEK) + KV; VaultSharp-compatible HTTP API |
| Identity | **OpenIddict** — auth code + PKCE for UIs, client-credentials for services; custom Dapper-backed `IOpenIddict*Store` implementations |
| Object store | MinIO (per-tenant bucket/prefix) + ClamAV (quarantine → scan → clean) |
| Web | Next.js App Router + TS; officer console MUI + React Query; borrower portal Tailwind (revisable) |
| Observability | OTel → Seq (logs) + Jaeger (traces) |
| Ports | api `:8200`, officer console `:3200`, borrower portal `:3300` |

## Security invariants

1. No plaintext PII at rest — field-level transit ciphertext + SQLCipher file
2. PII-free egress contract to MPEx — reflection-based CI guard ("PII firewall")
3. Zero PII in logs/traces — redaction filter + CI test
4. Append-only hash-chained audit per tenant + control plane
5. TLS on every hop; OIDC client-credentials between services
6. Immutable submitted application snapshots (append-only versions)
7. Secrets never in repo; gitleaks in CI
8. Masked-by-default UI; authorized reveal writes an audit event
9. Crypto-shredding erasure (key destruction before file deletion; Reg B 25-month retention first)
10. SQLite pragmas on every tenant connection: `secure_delete`, `auto_vacuum=FULL`, WAL, `foreign_keys`

## Milestones

Each milestone is a Linear ticket under parent MPE-63 and will receive its
own phase-level plan (`MPE-<n>-phase-<m>` branches) when scheduled.

| # | Milestone | Depends on |
|---|---|---|
| M0 | Workspace & tooling bootstrap (this work) — repo, `.devin` MCP config, CI/compose skeletons, **create milestone tickets** | — |
| M1 | Solution scaffold & architecture baseline — .NET 10 sln (`Lola.Api/Domain/ControlPlane/Infrastructure`), health, OTel, error model, security headers, CI green, compose stack | M0 |
| M2 | OpenBao & encryption foundation — transit mount, KEK→per-tenant-DEK, `IFieldCipher` + Dapper type handlers + `[PiiClass]` attributes, rotation/rewrap, crypto-shredding primitive | M1 |
| M3 | Control plane, OpenIddict identity & tenant provisioning — tenant registry, OIDC (custom Dapper stores), roles, borrower invite flow, tenant provisioning job, tenant resolution middleware | M2 |
| M4 | Tenant data plane — per-tenant SQLCipher files, pragmas, connection factory, DbUp rolling migrations across tenant files, encrypted backup/restore, isolation tests | M3 |
| M5 | MISMO-aligned domain model & intake API — relational + JSON1 snapshots, PII classification, draft→submit→review→decisioned status machine, immutable snapshots, validation | M4 |
| M6 | Audit, PII data-map & retention — hash-chained audit, PII read/write attribution, generated data-map, Reg B/GLBA retention engine, access-review report | M5 |
| M7 | Secure egress & MPEx integration — PII-free request model + mapper, PII-firewall CI guard, transactional outbox + worker, MPEx client (OIDC client-creds + TLS), result ingestion, `asOf` re-eval, retry/DLQ | M6 |
| M8 | ISPPr document integration — per-tenant client creds, doc packages (disclosures, 1003 summary, adverse action), payload minimization, presigned retrieval, delivery audit. **Note: e-signature is ISPPr-side future work** | M6 |
| M9 | Borrower document vault — MinIO per-tenant buckets, upload API (allowlist), ClamAV pipeline, ≤15-min presigned access, doc classification, retention hooks | M6 |
| M10 | Officer console — Next.js + MUI: OIDC login, pipeline board, application detail (masked PII + audited reveal), eval trigger/results, doc packages, audit viewer, tenant admin, Playwright e2e | M7–M9 APIs |
| M11 | Borrower portal — Next.js + Tailwind: invite acceptance, MISMO intake wizard, status tracker, doc upload, disclosure download, session hardening | M5, M8, M9 |
| M12 | Vendor adapter seam — `IVendorAdapter` interfaces (credit, VOE/VOI, flood, fraud, AUS), stub providers, OpenBao KV creds, async order lifecycle | M5+ |
| M13 | Hardening, UAT & close-out — STRIDE threat model, scan gates, backup/restore + crypto-shredding drill, DR runbook, dashboards, docs, size-and-shape report on MPE-63, close-out tickets (**ISPPr e-signature**, MPEx contract tests, vendor selection, ESIGN e-consent) | M10, M11 |

## Execution notes

- Phase 0 (M0) runs under parent **MPE-63** itself; milestone tickets are
  created at the end of M0 once the Linear MCP is configured.
- Team **MPE**, project **LOLA** (new). Branches `MPE-<n>-phase-<m>`; one PR
  per phase subticket; PRs never merge without explicit user approval.
- E-signature is intentionally out of scope — it belongs in **ISPPr** and
  gets filed there at LOLA close-out.
- Vendor services are adapter-seam only — no real bureau/VOE/flood accounts
  in this megaplan.
