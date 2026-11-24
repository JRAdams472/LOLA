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
| Integrations | MPEx (PII-free pricing egress), Zara (fee engine), ISPPr (document packages), vendor verification seam (credit/VOE/VOI/flood/fraud/AUS — stub adapter, real vendors later) |
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
internal-tool use. OIDC authorization-code + PKCE login mints tokens
that **never reach the browser**: the client holds only an opaque
`__Host-` session handle resolved per-request against the API's
`/bff-sessions` store (hashed handles, bounded idle/absolute expiry,
revoked on logout), and a same-origin BFF (`/bff/…`) injects `Bearer`
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

Borrower portal live (M12) — `clients/portal` is a second Next.js
app on the same token-custody architecture, hardened for the public
surface: an opaque `lola_portal` `__Host-` handle resolved against the
same server-side session store, a **30-minute idle window** (sliding
on resolve) under a **4-hour absolute bound** that touches cannot
extend, and a borrower-only read plane — GraphQL
`portalLoan(s)` returns a masked, borrower-safe projection while every
officer resolver and staff eval read (pricing, fees, steps, rate
lock, ULDD export) refuses borrower tokens. Surfaces: **invite
acceptance** (72h single-use hashed tokens), a five-section **intake
wizard** generated from the same MISMO catalog with masked-confirm
resume, save-as-you-go drafts, and validation deep-links; a
**dashboard** with a friendly 3-stage status tracker that never
exposes internal pipeline vocabulary; and **Documents** — upload to
the M10 vault (classification picker, friendly quarantine/clean/
rejected chips, retry) plus download of officer-generated disclosure
packages through audited 302 capability URLs. See
[`docs/m12-plan.md`](docs/m12-plan.md).

Vendor seam live (M13) — `VENDOR_ADAPTER=stub` selects the
deterministic verification provider (credit tri-merge, VOE, VOI,
flood cert, fraud/OFAC, AUS), shaped for real-vendor swap-in behind
`IVendorAdapter`. A per-kind egress allowlist (`VendorEgress` +
reflection firewall) pins exactly which PII fields each request may
carry — nothing more; `VendorRequestMapper` builds the typed request
from the application document, and staff-only
`POST …/{id}/vendor-orders` enqueues order + outbox in one
transaction. The registry-scoped `VendorOutboxWorker` resolves
credentials per call from OpenBao KV
(`lola-kv/vendors/{tenant}/{kind}` — never config/DB/repo), submits,
and completes over two channels: poll vendors keep the pending
outbox row as their poll handle (30s cadence, bounded →
timeout-fail); push vendors (`SupportsPush`) close it at submit and
complete through `POST /api/vendor-webhooks/{kind}` — HMAC-SHA256
`X-Vendor-Signature` over the raw body keyed by the tenant's KV
webhook secret, stream-bounded 64 KiB, dedicated per-IP+path rate
policy, and replay dedupe at the order level (a duplicate can never
write a second result or step event). Results land `vault:`-encrypted
behind a status-guarded transition with `Credit|Voe|…` service-step
events; officers read normalized PII-free summaries via staff-only
REST/GraphQL (`vendorOrders` returns `null` for borrowers) on the
console **Steps** tab. `VENDOR_STUB_WEBHOOK_URL` points the stub at
the real ingress for push-mode e2e. See
[`docs/m13-plan.md`](docs/m13-plan.md).

Hardening & close-out (M14) — [`docs/threat-model.md`](docs/threat-model.md)
maps STRIDE across all five PII flow stages (ingress, at-rest, egress,
logs, deletion) with per-threat mitigations and a residual-risk
register. CI enforces dependency-vuln gates
(`dotnet list package --vulnerable --include-transitive`,
`npm audit --audit-level=high`) alongside gitleaks, the coverage
floor, and the PII-firewall tests. `docs/openbao-runbook.md` carries
the DR section (sealed-vault recovery, multi-share unseal, key-loss
consequences, restore sequence); backup/restore, crypto-shred,
retention, and seal/unseal drills were executed live on scratch
tenants. [`docs/uat-matrix.md`](docs/uat-matrix.md) re-verifies every
milestone acceptance bullet with live evidence. See
[`docs/m14-plan.md`](docs/m14-plan.md).

Mutation event log + conditions (M15) — a second append-only
hash-chained table (`loan_events`) records every mutation as a typed
event (`field_change`, `override`, `transition`, `reveal`,
`condition_*`) written in the same `BEGIN IMMEDIATE` transaction as
the change itself — a failed mutation rolls its events back. PII-class
before/after values are stored as blind-index HMAC tokens, never
plaintext; non-PII values may be plaintext. Staff read the spine via
`GET …/applications/{id}/events` and verify the whole chain via
`GET …/events/verify` (clean or first-broken seq). The `conditions`
table tracks open→closed/waived lifecycle items with reasons and
audit linkage; staff mutate under `TenantStaff` while borrowers see a
stripped projection (kind/title/state/blocking only). See
[`docs/m15-plan.md`](docs/m15-plan.md).

Task engine + officer queue (M16) — a PII-free `tasks` table in the
**control plane** holds work items derived from tenant state by the
`TaskEvaluator`: open+blocking conditions, failed/referred pipeline
steps, TRID clock proximity (urgent ≤24h), and drafts stale >7d. Titles
are generated, never staff free-text — the queue is an egress boundary.
Evaluation runs on mutation paths and lazily on every queue read, so
time-driven tasks stay current with no worker. Officers read the ranked
list at `GET /api/tenants/{id}/tasks` (priority → due → seq, optional
`?skills=`), fetch the single highest item at `…/tasks/next`, and move
`open → claimed → done` via audited POST transitions (409 on another
officer's claim). GraphQL exposes `tasks`/`nextTask` staff-only; the
console's **Queue** page adds a "Do next" flow that claims the top task
and lands on its loan workspace. See
[`docs/m16-plan.md`](docs/m16-plan.md).

Validation feedback loop (M17) — downstream validators push structured
findings back into LOLA over a signed sibling ingress:
`POST /api/vendor-webhooks/{source}/validation` (`mpex`|`isppr` only —
unknown sources 404 before the body is read). The M13 webhook
discipline carries over: `X-Vendor-Signature` HMAC-SHA256 over the
exact raw bytes keyed by a per-tenant-per-source `webhook_secret`
(`vendors/{tenant}/{source}` in OpenBao KV), stream-bounded 64 KiB,
`vendor-webhook` rate policy. The payload-minimal `ValidationFeedback`
contract carries only references and enum tokens — the PII firewall
verifies it member-for-member (invariant 2). Receipts dedupe on
`(source, feedback_id)` in the tenant-side `vendor_feedback` ledger —
a race-safe `INSERT OR IGNORE`, so a replay answers
`200 {"status":"duplicate"}` with no second domain write. Findings map
to domain outcomes: `cleared` auto-closes its linked open condition;
`data_error`/`missing_field`/`exception` open `data_validation`
conditions linked to the receipt; `exception` (or `error` severity)
blocks and lands a high-priority `validation_exception` task on the
officer queue (skill by source: mpex→underwriting, isppr→processing);
advisory severities stay non-blocking. Every accepted receipt lands
`validation_received` on both the audit spine and the hash-chained
`loan_events`. Sibling teams implement against
[`docs/vendor-feedback-contract.md`](docs/vendor-feedback-contract.md);
see [`docs/m17-plan.md`](docs/m17-plan.md).

Document intelligence + auto-conditions (M18) — the vault scan drain
now classifies between ClamAV's Clean verdict and promotion:
`quarantine → scan → classify → promote`. `DOC_CLASSIFIER` selects the
`IDocClassifier` implementation (`rule` = deterministic table over
filename/mime/text markers — the default, no engine, no model;
`stub` = seam test double that echoes the declared kind). The
classifier reads a bounded 2 MiB plaintext head tee'd off the
decrypt→INSTREAM stream — bytes never touch durable storage — and
produces a detected kind, confidence, extracted fields, and findings
(`expired_doc`, `kind_mismatch`, `unreadable`, `missing_field`).
Extracted fields are PII-class, so the whole `DocClassification` lands
in `vault_docs.classification_json` as `vault:` ciphertext; events and
audit carry metadata only (kind, confidence, finding names — a test
plants a sentinel value and proves it absent from every row).
Findings derive conditions through the existing M15/M16 machinery
(actor `auto:classifier`): a matching kind auto-closes the linked
`doc_required`; `expired_doc`/`kind_mismatch`/`unreadable` open
blocking replace-doc conditions pinned to the specific `vault_doc`;
`missing_field` opens a non-blocking `data_validation`; coarse
employer↔property locality divergence opens a non-blocking
`distance_check`. Replays converge — no duplicate accumulation.
Officers see names-only intel chips on vault rows (`read as w2 ·
100%`, finding chips, extracted field names — never values);
borrowers see a "What we need" section on the portal documents page
backed by the stripped `BorrowerConditionDto` projection. Upload-side
`bank-statement` and classifier-side `bank_statement` reconcile
through `DocKind.Normalize`. A classifier failure is audited and the
doc still promotes — AV remains the only pipeline gate. See
[`docs/m18-plan.md`](docs/m18-plan.md).

Ops docs: [`docs/openbao-runbook.md`](docs/openbao-runbook.md) ·
[`docs/identity-runbook.md`](docs/identity-runbook.md) ·
[`docs/data-plane-runbook.md`](docs/data-plane-runbook.md) ·
[`docs/threat-model.md`](docs/threat-model.md) ·
[`docs/uat-matrix.md`](docs/uat-matrix.md) ·
[`docs/vendor-feedback-contract.md`](docs/vendor-feedback-contract.md)

## Observability

The compose stack includes **Jaeger** (traces, http://localhost:17686) and
**Seq** (structured logs, http://localhost:5441) — remapped so LOLA runs
alongside the sibling stacks. The API exports only when
the `OTEL_*` env vars are set — exporter-free otherwise. **PII never enters
logs or spans** — a redaction filter enforces this in CI.
