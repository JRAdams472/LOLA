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
| Integrations | MPEx (PII-free pricing egress), Zara (fee engine), ISPPr (document packages) |
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

Fees live (M8) — `FEE_ADAPTER=zara` selects the Zara engine
(`stub` is the deterministic dev default): the PII-free
`FeeScenario` (loan purpose/type/amount/term, property
state/county/purchase-price/est-value, VA, discount points —
the adapter strips ZIP for Zara's own guard) evals through a
per-tenant `fee_outbox` with the pricing worker's semantics
(backoff → DLQ, request-hash idempotency). `POST …/fees` →
202 + eval id, `GET …/fees[/history]` reads the spine; line
items normalize into LE cost blocks (A/B/C/E/F). `PATCH
…/fees/{itemId} {amount, reason}` sets/clears an audited
override that survives re-evaluation keyed by (feeCode,
section) — orphans surface as `staleOverrides`, never
silently dropped. `POST …/fees {variants:[{label?, loanType?,
discountPoints?}]}` runs several scenario variants in one
batch — FHA vs conventional side-by-side — draining in
parallel; `GET …/fees` returns `FeeComparisonDto` with
breakdowns keyed by `variant` and overrides scoped so one
variant's adjustment can't leak into another's.

Documents live (M9) — `DOC_ADAPTER=isppr` selects the ISPPr document
engine (`stub` is the deterministic dev default): per-tenant
provisioning binds an ISPPr client/customer plus a generate-scoped
`X-API-Key` wrapped under the tenant's transit key, and uploads the
four manifest-declared template masters. `POST …/packages` enqueues
a manifest-bounded payload — `le-package` gates on an active rate
lock + completed fee eval; `adverse-action` needs neither but takes
enum-validated reason codes — and a per-tenant `doc_outbox`
(submit → poll, backoff → DLQ, request-hash dedupe) drives the
vendor call off-request. The tenant-file `documents` registry
stores references only (vendor request id + filename — never
bytes); list/detail reads sit inside borrower-own scoping, and
`GET …/documents/{docId}/download` mints a fresh ≤15-min presigned
URL per call as a 302 with `doc_download` audited on the
application spine. `POST /api/admin/tenants/{id}/isppr/republish-templates`
(PlatformOps) bumps template versions without re-provisioning, and
tenant erasure revokes the vendor key before crypto-shredding.

Borrower document vault (M10) — `VAULT_ADAPTER=minio` selects the real
pipeline (`stub` seals uploads in-memory for dev/tests): uploads are
streamed multipart — never buffered, never a temp file — through a
magic-byte allowlist (pdf/png/jpg/tiff/docx; bytes decide, declared
claims are hints) into `VaultCipher`, a chunked AES-256-GCM seal whose
per-object DEK is wrapped under the tenant's transit key. Ciphertext
lands in the per-tenant MinIO bucket `lola-vault-<tid>` under
`quarantine/`; a `vault_scan_outbox` row enqueues the AV pass in the
same transaction, and `VaultScanWorker` (`VAULT_SCAN_ENABLED` /
`VAULT_SCAN_INTERVAL_MS`) drains it — decrypt-stream → raw-TCP
`zINSTREAM` to clamd → clean verdict promotes via `CopyObject` to
`clean/` and destroys the quarantine source, infected verdict marks
`infected` and destroys it (never reaches `clean/`), and scanner
failures backoff to a bounded DLQ — scanning fails closed. The
`vault_docs` registry holds references + ciphertext metadata only.
Downloads are minted per call: `GET …/uploads/{id}/download` audits
`vault_download` and 302s to a ≤15-min capability URL signed by a
transit HMAC under the tenant's index key (hard-clamped even if
`VAULT_URL_TTL_SECONDS` is misconfigured); the auth-free
`/api/vault-content/{id}?tid&exp&sig` route re-verifies and streams
the decrypt path with `Cache-Control: no-store`, RFC5987 filename, and
a uniform 403 for every bad input — the signature dies with the
tenant's keys on erasure, which also purges objects + bucket
fail-closed before the crypto-shred.

Officer console live (M11) — `clients/web` is a Next.js (App Router)
loan-officer console over the live API, GitHub-dark and dense for
internal-tool use. OIDC authorization-code + PKCE login mints an
access token that **never reaches the browser**: a sealed JWE session
cookie backs a same-origin BFF (`/bff/…`) that injects `Bearer`
server-side — vendor endpoints (MPEx/Zara/ISPPr/MinIO) are provably
unreachable from the page (e2e asserts zero direct calls, ADR-0007).
Surfaces: a **Pipeline** of active loans with borrower, status, step,
and a business-day TRID countdown; a **Loan Workspace** with
generated MISMO/1003 tabs (`mismo-field-map.json` →
`lib/field-map.ts`), dirty-tracked merge-patch edits on drafts,
audited corrections on submitted apps, masked-by-default PII with an
explicit reason-gated reveal that lands on the Audit tab, plus Steps
history; **Closing Costs & Docs** — LE fee sections with 202-poll
recalc and reason-gated overrides, pricing offers with lock/unlock,
ISPPr package generation, and BFF-relayed 302 downloads for
generated docs and borrower-vault uploads; **Search** by blind-index
field (name/SSN/phone/email/property) via GraphQL POST — the term
never reaches a URL; and an **Admin** console (users, groups +
membership, invites, tenant audit, retention) gated by role.
Reads are GraphQL (depth 8, cost ≤300, ADR-0011) over the same
Wolverine handlers as REST; mutations stay REST. See
[`docs/m11-plan.md`](docs/m11-plan.md).

Ops docs: [`docs/openbao-runbook.md`](docs/openbao-runbook.md) ·
[`docs/identity-runbook.md`](docs/identity-runbook.md) ·
[`docs/data-plane-runbook.md`](docs/data-plane-runbook.md)

## Observability

The compose stack includes **Jaeger** (traces, http://localhost:17686) and
**Seq** (structured logs, http://localhost:5441) — remapped so LOLA runs
alongside the sibling stacks. The API exports only when
the `OTEL_*` env vars are set — exporter-free otherwise. **PII never enters
logs or spans** — a redaction filter enforces this in CI.
