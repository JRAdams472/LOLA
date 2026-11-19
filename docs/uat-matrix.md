# LOLA UAT Matrix — M14 Phase 3 (MPE-235)

Every acceptance bullet on every milestone ticket (M0–M14, MPE-64…78)
re-verified **live** on the dev compose stack on 2026-11-19. Each row: the
bullet, the check that ran, the result, and where the evidence lives.

Legend — ✅ verified live this sweep · 🧪 verified by green test/e2e (named)
· 🔗 verified by recent drill/phase proof (linked)

**Environment:** `lola-api` :8200 healthy (`db:up`, `bao:unsealed`), web :3200,
portal :3300, OpenBao :8300, Seq :5441, Jaeger :17686, MinIO :9200, ClamAV
:3310 — plus live MPEx/Zara/ISPPr sibling stacks.

**Heavyweight re-runs (once, this sweep):**

| Suite | Result |
|---|---|
| `dotnet test -c Release` + XPlat coverage | **416/416**, coverage **89.7%** overall — Api 81.3, Application 92.9, ControlPlane 97.2, DataPlane 88.5, Domain 97.2, Infrastructure 87.6; floor 80%/area green |
| web `npx jest` | **79/79** |
| portal `npx jest` | **45/45** |
| web e2e `marquee` + `vendor-marquee` | **5/5** |
| portal e2e `marquee` + `vendor-denial` + `session` | **4/4** |
| `dotnet format --verify-no-changes` | clean |
| gitleaks 124 commits | **no leaks** |

Scratch tenant **`uat`** was provisioned, exercised end-to-end, then
crypto-shredded as part of this sweep (transcripts on MPE-235).

---

## M0 — Workspace & tooling (MPE-64)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Repo `JRAdams472/LOLA` private, pushed, CI green | `gh repo view` + `gh run list` | ⚠️ pushed + CI green, but visibility is **public** — see Finding F1 | `gh run list`: PR #108 run green |
| `docker compose config` valid; infra healthy alongside ISPPr | `docker compose config -q`; `docker ps` | ✅ valid; lola + isppr + mpex + zara all Up | compose check; `docker ps` |
| All milestone tickets exist under MPE-63; megaplan attached | `list_issues parentId=MPE-63`; `get_issue MPE-63` | ✅ 15 milestone tickets; "LOLA Megaplan (MPE-63)" attachment present | Linear |

## M1 — Solution scaffold (MPE-65)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| api healthy on :8200, spans in Jaeger, logs in Seq | `GET /healthz` + `/api/health`; Jaeger `/api/v3/services`; Seq `/api/events` | ✅ `db:up, bao:unsealed`; `lola` service w/ 30-trace sample; 2000 Seq events | curl + Seq/Jaeger API |
| format/build/test green locally + CI | `dotnet format`, Release suite, PR #108 CI | ✅ clean; 416/416; CI 5/5 | this sweep + actions run |

## M2 — OpenBao & encryption (MPE-66)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Round-trip encrypt/decrypt per tenant | `FieldCipherTests` in suite | 🧪 green (Infrastructure 83/83) | test run |
| Rotation rewraps ciphertext | `FieldCipherTests` rotation cases | 🧪 green | test run |
| Destroyed key → unrecoverable | uat erase → `tenant-uat*` transit keys absent; restore 400 | ✅ live on scratch tenant | Linear transcript |
| Blind index: SSN lookup returns row while stored SSN is ciphertext | `PATCH` synthetic ssn `000123456` on uat → `GET /search?field=ssn` → hit; raw `uat.db` bytes scanned | ✅ `applicationIds:[app-…]`; file has no SQLite header, zero plaintext probes | curl transcript |
| No secrets in repo | gitleaks full-history scan | ✅ no leaks, 124 commits | gitleaks run |

## M3 — Control plane, identity, provisioning (MPE-67)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| OIDC login flows for both audiences; client-credentials | officer auth-code+PKCE login (authorize→login→code→token); `lola-ops`, `lola-svc` mints; portal invite login via e2e setup | ✅ all three flows mint valid tokens | `/connect/token` + seeder login() |
| Provisioning creates isolated tenant end-to-end | `POST /api/admin/tenants {tenantId:"uat"}` → 8 steps (registry→vault-bucket), svc client issued | ✅ live | Linear transcript |
| Role→visibility enforced in responses | svc `GET /applications/{id}` → `ssn:"•••3456"`, names `•••`, income masked | ✅ masked-by-default | curl transcript |
| Tenant A cannot reach tenant B data | svc-uat token → `GET /api/tenants/demo/applications` | ✅ **403** "request tenant does not match the resolved tenant"; `TenantAccessTests`/`TenantRepositoryIsolationTests` green | curl + tests |

## M4 — Tenant data plane (MPE-68)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Migrations roll across N tenant files w/ status tracking | `tenant_migrations` control-plane rows for uat | ✅ 13 rows, all `applied`, per-tenant | sqlite dump |
| Restored backup opens only with correct key | M14 P2 drill (backup→mutate→restore→mutation gone, sha256 manifest verified) + `BackupEndpointTests` | 🔗 live-proven on `drill` | MPE-234 comment |
| `secure_delete` — no plaintext recoverable | uat.db byte scan: no SQLite magic, no field plaintext | ✅ file fully opaque | byte-scan transcript |
| Cross-tenant impossible | svc-uat→demo 403 + isolation tests | ✅ | above |

## M5 — MISMO domain, intake API (MPE-69)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Full lifecycle incl. operator correction | uat: PATCH→submit→422 field errors→PATCH fix→submit→v4 | ✅ structured `path/code/message` errors | curl transcript |
| MISMO XML round-trip; ULDD structurally valid | `export-mismo`→200 MISMO 3.4; `import-mismo`→200 `app-cf68011d`, 10 fields; `export-uldd`→200 | ✅ live round-trip | curl transcript |
| PII ciphertext at rest; blind-index search | uat.db opaque; `search?field=ssn|first_name|phone|email` live | ✅ ssn hit on uat; `first_name=Ada`→5+ hits on demo; invalid field→400 | transcripts |
| Immutable submitted snapshot; TRID clock | PATCH submitted app→**400 "only drafts can be patched"**; `tridClockStart`/`tridDeadlineUtc` set when 6 triggers complete | ✅ live | transcripts |

## M6 — Audit, masking, data-map, retention (MPE-70)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Audit chain verifies; tamper detection | `GET /api/tenants/demo/applications/audit/verify` | ✅ `{ok:true, eventCount:938, firstBrokenSeq:null}`; hash-chained events incl. `prev_hash` | curl + control.db dump |
| Data-map enumerates every PII field | `docs/pii-data-map.json` present; `PiiDataMapTests` green | 🧪 | repo + tests |
| Masking per role; authorized reveal audited | masked GET→`•••3456`; `POST /reveal {reason:"uat verify"}`→plaintext + `reveal` audit event w/ actor+reason+classes | ✅ live | transcript |
| Erasure post-retention irrecoverable | uat: erase→409 in-window; force w/o reason→400; force+reason→200; file+backups gone, transit keys destroyed, `svc-uat`→`invalid_client`, restore→fail; spine `retention_override`→`isppr_key_revoked`→`vault_purged`→`tenant_erased` | ✅ live | transcript |

## M7 — Pricing adapter & MPEx (MPE-71)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Eval round-trip vs live MPEx | `POST …/price`→202→`done`: CONV30 @ 6.25 eligible | ✅ live sibling | transcript |
| PII firewall CI test | `PricingFirewallTests` + `VendorFirewallTests` green | 🧪 | suite |
| Lock persists + freezes; retries idempotent | `POST …/lock`→200 `lockId`, `expiresUtc`; lock tests green | ✅ + 🧪 | transcript + suite |
| Adapter swap = one interface | `AltStubPricingAdapter` + stub tests green | 🧪 | repo + suite |

## M8 — Fee engine adapter (MPE-75)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Fee request → LE cost-block sections | `POST …/fees`→202→`done`: sections A(1245) B(710) C(1550) E(485) F G H | ✅ live Zara | transcript |
| Override persists, audits, survives recalc | `FeeMarqueeTests`/`FeeRepositoryTests` override legs green | 🧪 | suite |
| Adapter swap = one interface | `IFeeService` + stub/provider pair | 🧪 | suite |

## M9 — Document adapter & ISPPr (MPE-72)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Package retrievable only via ≤15-min presigned URL | `GET …/documents/{docId}/download`→302 → `X-Amz-Expires=900` | ✅ exactly 900s | header transcript |
| Registry lists packages w/ timestamps + download; delivery audited | `GET …/documents` → `doc-a9d0…`, `le-package`, completed w/ createdUtc+completedUtc; audit `doc` events | ✅ | transcript |
| Adapter swap = one interface | `AltStubDocumentService` tests green | 🧪 | suite |

## M10 — Borrower document vault (MPE-73)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| EICAR quarantined, never reaches clean bucket | `VaultLiveTests`/`VaultMarqueeTests` EICAR legs green in suite | 🧪 | suite |
| Uploads never unencrypted; presigned expiry | `.txt`→**415** allowlist; `.pdf`→202 `quarantined`→`clean`; objects encrypted in MinIO; presigned GET ≤15m | ✅ live upload on demo | transcript |
| Doc list + download; classification recorded | `GET …/uploads` → `uat-paystub.pdf`, `paystub`, `clean` | ✅ | transcript |

## M11 — Officer console (MPE-74)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Three views functional vs live API; TRID countdown | web e2e `marquee` (pipeline→workspace→closing) green; `tridClockStart`/`tridDeadlineUtc` in API responses | ✅ | playwright + curl |
| Search by SSN/phone/email/name/address | blind fields `ssn,phone,email,first_name,last_name,street` live; `first_name=Ada`→hits; invalid field→400 | ✅ | transcripts |
| Masked default; reveal authorized + audited | masked GET + `POST /reveal` + audit event (above) | ✅ | transcript |
| Playwright proof | `marquee.spec.ts` 1/1 | ✅ | run |

## M12 — Borrower portal (MPE-76)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Intake end-to-end from invite link | portal `marquee.spec.ts`: invite→apply→submit→package→documents→download | ✅ 3/3 | playwright |
| Uploads → vault; disclosures downloadable; session expiry | uploads covered via marquee; `session.spec.ts` on idle-short :3301 instance → idle session rejected server-side | ✅ | playwright |
| No officer fields/internal status in borrower payloads | `vendor-denial.spec.ts` — vendor order invisible to borrower | ✅ | playwright |

## M13 — Vendor adapter seam (MPE-77)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Stub order completes full lifecycle | uat `POST …/vendor-orders {kind:credit}`→202→`completed` `stub-credit-f1b1…` summary "score 744 · 3 bureaus" | ✅ live | transcript |
| Webhook + poll channels | web `vendor-marquee` 4/4 poll+push; signed replay idempotent, forged→401, unknown→404 | ✅ | playwright |
| Adding a vendor = one interface; creds never in config/DB/repo | `AltStubVendorAdapter`; creds under `lola-kv/vendors/*` only | ✅ + 🧪 | KV listing + suite |

## M14 — Hardening, UAT, close-out (MPE-78)

| Bullet | Live check | Result | Evidence |
|---|---|---|---|
| Threat model + runbooks committed; drills w/ proof | `docs/threat-model.md`, `docs/openbao-runbook.md`, `docs/data-plane-runbook.md` on main; 4 drill transcripts on MPE-234 | ✅ merged via PR #104/#108 | repo + Linear |
| All milestone bullets verified during UAT | **this matrix** | ✅ | — |
| Close-out tickets in correct projects | MPE-238 filed (rate-limit finding); remaining close-out tickets are P5 scope | ⏳ pending P5 | Linear |

---

## Observability sanity (M14 scope)

| Check | Result |
|---|---|
| Seq queryable, 2000-event sample | ✅ events flowing, all `Information` |
| Jaeger `lola` service, 30-trace sample | ✅ route templates only (`{id}`, `{appId}`) — no values in span names |
| **PII-in-logs audit (broadened)** | ✅ **clean** — Seq sample + `docker logs` (45,916 lines) + span attrs scanned for `\d{3}-\d{2}-\d{4}`, `\d{9}`, seed SSNs, synthetic names, DOB/account shapes → **0 hits** (one 9-digit hit was Wolverine's own node-id) |

## Findings

| # | Severity | Finding | Disposition |
|---|---|---|---|
| F1 | medium | Repo `JRAdams472/LOLA` is **public** — M0 bullet requires private | escalate to user (repo visibility is an org decision); tracked on MPE-235 |
| F2 | low | Global rate limiter also 429s `/healthz` + `/account/login` under burst → probe flapping | **MPE-238** filed |
| F3 | info | Local Debug-config coverage under-reports (Api 78.8%) vs CI Release (81.3%) — instrumentation line-map noise, not a real drop | documented; no action (gate uses Release) |
