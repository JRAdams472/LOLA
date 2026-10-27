# LOLA Megaplan — MPE-63

Multi-tenant, MISMO-aligned **loan origination system** — the PII-bearing
front end of the mortgage pipeline. C#/.NET 10 API, Dapper + DbUp, SQLite
file-per-tenant data plane (SQLCipher + per-tenant OpenBao DEKs + field-level
transit encryption on PII-class columns), SQLite control plane, OpenIddict
identity, Next.js officer console + borrower portal, pluggable-adapter
integrations to MPEx (PII-free pricing) and ISPPr (document packages),
fee-engine adapter, MinIO + ClamAV document vault, hash-chained audit,
crypto-shredding retention.

MVP data flow (per MPE-63):
`POS Intake → Validate 1003 → PPE API → Fee API → Doc Engine → Package`

## Architecture (decided)

| Layer | Choice |
|---|---|
| API | ASP.NET Core (.NET 10 LTS) |
| Data access | **Dapper** + `Microsoft.Data.Sqlite` + `SQLitePCLRaw.bundle_e_sqlcipher`; **DbUp** append-only `migrations/NNNN_name.sql` — no EF Core |
| Data plane | SQLite — one encrypted file per tenant; canonical MISMO application **documents** in JSON1 payload columns + relational index columns |
| Control plane | SQLite (tenant registry, users, OIDC clients) |
| Keys/secrets | **OpenBao** transit (KEK → per-tenant DEK) + KV; field-level `[PiiClass]` encryption + **blind indexes** for searchable PII |
| Identity | **OpenIddict** — auth code + PKCE for UIs, client-credentials for services; custom Dapper-backed `IOpenIddict*Store` implementations |
| Integration seams | `IPricingService`, `IFeeService`, `IDocumentService`, `IVendorAdapter` — DTS-style pluggable vendors (MPEx / ISPPr first) |
| Object store | MinIO (per-tenant bucket/prefix) + ClamAV (quarantine → scan → clean) |
| Web | Next.js App Router + TS; officer console MUI + React Query; borrower portal Tailwind (revisable) |
| Observability | OTel → Seq (logs, :5441) + Jaeger (traces, :17686) |
| Ports | api `:8200`, officer console `:3200`, borrower portal `:3300`, openbao `:8300`, minio `:9200/:9201`, clamav `:3310` |

## Security invariants

1. No plaintext PII at rest — field-level transit ciphertext + SQLCipher file
2. PII-free egress contract to MPEx — reflection-based CI guard ("PII firewall"); **no data in flight that isn't needed**
3. Zero PII in logs/traces — redaction filter + CI test
4. Append-only hash-chained audit per tenant + control plane
5. TLS on every hop; OIDC client-credentials between services
6. Immutable submitted application snapshots (append-only versions)
7. Secrets never in repo; gitleaks in CI
8. **Masking/suppression is the default state** — role→visibility map; authorized reveal writes an audit event
9. Crypto-shredding erasure (key destruction before file deletion; Reg B 25-month retention first)
10. SQLite pragmas on every tenant connection: `secure_delete`, `auto_vacuum=FULL`, WAL, `foreign_keys`

## Milestones — Linear tickets

Each milestone is a Linear issue under parent **MPE-63** (project **LOLA**,
team MPE) and receives its own phase-level plan (`MPE-<n>-phase-<m>`
branches) when scheduled. LOLA-Mn slugs carry ordering.

| # | Milestone | Ticket | Depends on |
|---|---|---|---|
| M0 | Workspace & tooling bootstrap — **Done** | MPE-64 | — |
| M1 | Solution scaffold & architecture baseline — .NET 10 sln, health, OTel, error model, security headers, CI green, compose api | MPE-65 | M0 |
| M2 | OpenBao & encryption foundation — transit, KEK→tenant DEK, `IFieldCipher` + type handlers + `[PiiClass]`, **blind indexes** for searchable PII, rotation/rewrap, crypto-shredding | MPE-66 | M1 |
| M3 | Control plane, OpenIddict identity & tenant provisioning — registry, OIDC (custom Dapper stores), roles/groups→visibility, borrower invites, provisioning job | MPE-67 | M2 |
| M4 | Tenant data plane — SQLCipher per-tenant files, pragmas, connection factory, DbUp rolling migrations, backup/restore, isolation tests | MPE-68 | M3 |
| M5 | MISMO domain, intake API & document store — MISMO docs + relational index, **MISMO XML import/export + ULDD export**, TRID six-field clock, validation/correction, immutable snapshots, state machine `POS→Validate→PPE→Fee→Doc→Package`, workflow-orchestration seam | MPE-69 | M4 |
| M6 | Audit, masking layer, PII data-map & retention — hash-chained audit, role-based response masking (default masked), generated data-map, Reg B/GLBA retention | MPE-70 | M5 |
| M7 | Pricing adapter & MPEx — `IPricingService` seam, PII-free mapper + firewall, normalized rates, **rate Lock** (freezes locked fields), outbox, `asOf` re-eval | MPE-71 | M6 |
| M8 | Fee engine adapter — `IFeeService` seam, itemized fees → LE sections A/B/C/E, fee aggregator + editable override toggles (audited) | MPE-75 | M6 ∥ M7 |
| M9 | Document adapter & ISPPr — `IDocumentService` seam, master payload (1003+pricing+fees → LE/disclosures), presigned retrieval, doc registry. **e-sign is ISPPr-side future work** | MPE-72 | M6, M7, M8 |
| M10 | Borrower document vault — MinIO per-tenant, upload allowlist, ClamAV quarantine→clean, ≤15-min presigned, classification | MPE-73 | M6 |
| M11 | Officer console — **Pipeline** (TRID countdown), **Loan Workspace** (tabbed 1003, validation correction), **Closing Costs & Docs** (fees/lock/package 3-col), loan search (name/**SSN**/phone/email/address), masked-by-default + audited reveal | MPE-74 | M5–M10 |
| M12 | Borrower portal — invite flow, MISMO intake wizard, status, doc upload, disclosure download, masked display, session hardening | MPE-76 | M5, M9, M10 |
| M13 | Vendor adapter seam — credit/VOE/flood/fraud/AUS interfaces + stubs, OpenBao KV creds, async order lifecycle | MPE-77 | M5+ |
| M14 | Hardening, UAT & close-out — STRIDE threat model, drills, runbooks, wiki refresh (synthetic data), size-and-shape report, close-out tickets (**ISPPr e-signature**, MPEx contract tests, vendor selection, workflow orchestration UI) | MPE-78 | M11, M12 |

## Notes

- Phase 0 (M0) ran under parent MPE-63; tickets were created at its end.
- **Every milestone ends with a hardening gate** — OWASP + application
  hardening + architecture/ADR review; findings post as a ticket comment
  before the milestone can close (per `AGENTS.md`). M14 is the deep pass
  across the whole system on top of the per-milestone gates.
- Spec-folds applied vs. the original draft: fee engine elevated to its own
  milestone; `I*Service` adapter seams formalize the DTS-style pluggable
  requirement; "documents in a nosql data structure" = canonical MISMO
  documents in JSON1 payload columns + relational index (preserves field
  crypto, schema-as-control, queryability); masking-by-default + blind
  indexes; ULDD export added to M5.
- E-signature is intentionally out of scope — it belongs in **ISPPr** and
  gets filed there at LOLA close-out.
- Vendor services are adapter-seam only — no real bureau/VOE/flood accounts
  in this megaplan.
