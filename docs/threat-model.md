# LOLA Threat Model (STRIDE)

**Scope:** every flow that touches protected personal data (PII) in LOLA —
ingress, at-rest, egress, logs/traces, deletion. Written for M14 (MPE-78)
as the deep-pass counterpart to the per-milestone hardening gates.

**Assets under protection:**

- Borrower PII — names, SSN-class identifiers, DOB, contact data, financial
  fields in MISMO applications (`PiiClass`-tagged).
- Cryptographic material — per-tenant file keys (SQLCipher), per-tenant
  DEKs (OpenBao transit), OpenBao unseal shares/root token, OIDC client
  secrets, vendor credentials, HMAC webhook secrets.
- Identity artifacts — OIDC access/refresh tokens held server-side behind
  opaque BFF session handles (control-plane `bff_sessions`, transit
  ciphertext).
- Audit chain — append-only hash-chained records proving who saw what.
- Derived artifacts — document payloads (ISPPr/MinIO vault), backups,
  vendor result documents.

**Method:** for each flow stage, the trust boundary, applicable STRIDE
categories, the mitigation that exists *today* (with anchor), and residual
risk / follow-ups. Anchors: `INV-n` = AGENTS.md security invariant;
`ADR-nnnn` = `docs/adr/`; `Rn` = runbook procedure.

---

## 0. System diagram (trust boundaries)

```
                        ┌───────────────────── INTERNET ─────────────────────┐
  Officer ──TLS──▶ web BFF (Next) ──opaque handle──┐                         │
  Borrower ─TLS──▶ portal BFF (Next) ─opaque handle┤                         │
                        │                       ▼                            │
                        │              ┌───── LOLA api ──────┐               │
                        │              │ REST + GraphQL      │               │
                        │              │ OIDC (OpenIddict)   │               │
                        └─────────────▶│ tenant middleware   │               │
                                       └──────┬───────┬──────┘               │
                          ┌───────────────────┼───────┼──────────┐           │
                          ▼                   ▼       ▼          ▼           │
                    control plane      data plane   OpenBao   Seq/Jaeger     │
                    (registry, keys)   (SQLCipher   (transit,  (logs, traces) │
                                       per tenant)  KV, PKI)                 │
                                       │                                     │
              ┌────────────────────────┼─────────────────────────┐           │
              ▼            ▼           ▼            ▼            ▼           │
            MPEx        Zara        ISPPr        MinIO      Vendor web       │
          (pricing)   (fees)     (doc pkgs)     (vault)     /-webhooks       │
                        └───────────────────── EGRESS ──────────────────────┘
```

Trust boundaries crossed by PII: **B1** browser↔BFF, **B2** BFF↔api,
**B3** api↔tenant file/OpenBao, **B4** api↔external services (MPEx/ISPPr/
Zara/vendors/MinIO), **B5** api↔observability sinks, **B6** api↔storage
lifecycle (backup/erase).

---

## 1. Ingress — data entering LOLA

**Flows:** officer console mutations + reveals, portal wizard PATCHes,
document uploads, vendor webhook completions, admin tenant/user ops.

| Threat | Vectors | Mitigations (anchor) | Residual |
|---|---|---|---|
| **S**poofing — forged officer/borrower identity | Stolen session, forged OIDC token, webhook forgery | OIDC login at BFF; session custody is an opaque `__Host-` handle — tokens live only in the control-plane `/bff-sessions` store as transit ciphertext and never reach the browser (INV-7 custody); handles are namespace-scoped (console handle can't resolve portal rows) and revocation is `auth_version`-checked; role claims checked server-side per handler (`isBorrower` early-outs); webhook HMAC `X-Vendor-Signature` over raw body, constant-time compare, per-tenant secret from OpenBao KV | Session theft via endpoint compromise — mitigated by opaque handles + server-side revocation; no proof-of-possession binding beyond TLS |
| **T**ampering — mutated intake/upload payload in flight | MITM, replayed webhook, draft overwrite | TLS every hop (INV-5); webhook replay idempotent + external-id resolution (single-open→complete, all-terminal→duplicate, ≥2 open→409 Ambiguous); submitted snapshots immutable (INV-6); draft PATCH merge inside `BEGIN IMMEDIATE` tenant txn | Replay within rate-limit window is a no-op by design; multi-tab draft edits serialize but last-writer-wins semantics stand |
| **R**epudiation — actor denies an action/reveal/read | "I didn't reveal that SSN" | Hash-chained append-only audit on every PII read/write/reveal — actor, tenant, entity, field class, reason (INV-4); audit rows never updated/deleted; forced-retention-erase writes `retention_override` | Audit proves API-attributed actor, not human identity beyond OIDC subject |
| **I**nformation disclosure — over-read at ingress | Borrower reading staff surfaces, cross-tenant read | Tenant resolved once in middleware; every data-plane query scoped to that tenant's file — no path can name another file (convention); borrower early-outs return `null`/403 before handler runs; serialized-payload tests forbid vendor/result vocabulary in borrower DTOs | GraphQL field-level leakage is bounded by resolver guards; new resolvers must repeat the pattern (CI-tested via contract tests) |
| **D**enial of service — ingress flooding | Webhook floods, giant uploads, draft spam | Per-IP/path rate limits on webhook ingress (60/min); webhook body bounded 64 KiB declared+chunked; upload allowlist + size caps + ClamAV quarantine→clean before vault; RFC 9457 errors | Login endpoint throttling is default ASP.NET rate-limit policy; no WAF in this stack |
| **E**levation — borrower → staff ops; member → admin | Role confusion, admin endpoint reach | Role/group→visibility checks server-side; admin endpoints require admin role; invite acceptance is single-use + expiry; staff-only queries (`retention`, `vendorOrders`, audit) check claims before repository access | Open follow-up MPE-225: officer vocabulary still present in borrower own-application REST projections (denial in place; vocabulary cleanup ticketed) |

## 2. At-rest — data stored in LOLA

**Flows:** tenant application data, PII columns, audit chain, documents
(MinIO), backups, OpenBao key material, KV secrets.

| Threat | Mitigations (anchor) | Residual |
|---|---|---|
| **S**poofing — attacker opens tenant DB directly | File is SQLCipher-encrypted with a per-tenant key stored *wrapped* in the control-plane registry; unwrapping requires OpenBao transit decrypt under that tenant's transit key (INV-1, ADR-0003/0004). Stealing `data/tenants/*.db` + registry yields ciphertext twice over | Root token in `data/openbao/` is dev-only (single share, gitignored, mode 600); production unseal/key custody is a runbook concern (Phase 2 DR section) |
| **T**ampering — silent modification of DB file, audit chain rewrite | Field-level ciphertext tamper → transit decrypt fails loudly; audit chain is hash-chained — rewriting a row invalidates subsequent links (INV-4); migrations append-only (DbUp); SQLite `secure_delete=ON`, `foreign_keys=ON`, WAL discipline (INV-10) | Hash-chain verification is structural (no periodic external attestation); accepted — chain tamper is detectable on read-verify |
| **R**epudiation | Hash-chained audit attributes every PII read/write to actor+reason (INV-4); retention-overrides audited | — |
| **I**nformation disclosure — file/registry/backup exposure | PII columns are transit-envelope ciphertext inside the already-encrypted file — two layers, separate key domains (INV-1); backups are `VACUUM INTO` snapshots encrypted with the *same* tenant key, `journal_mode=DELETE`, sha256 manifest verified before restore; MinIO objects per-tenant, ≤15-min presigned reads; field masking means app-layer reads default to masked (INV-8) | `data/` directory itself is host-local; host compromise below encryption layers is out of model (kernel/fs controls) — noted as environment assumption |
| **D**enial of service — key loss, vault seal | OpenBao seal → transit ops fail → api degrades predictably (drill in Phase 2); backups encrypted with tenant keys → key destruction = intentional unrecoverability (that's the shred guarantee); OpenBao DR runbook covers seal/unseal + restore | Unseal key + root token co-located on host in dev (documented dev trade-off); production split-custody documented in DR runbook |
| **E**levation — bypassing tenant scoping at storage layer | One file per tenant; connection factory resolves the file from tenant context set in middleware — repositories cannot name a path; `TenantContext`/`CipherContext` required before any crypto op (worker bug M13 proved the guard trips loudly) | — |

## 3. Egress — data leaving LOLA

**Flows:** MPEx pricing/eligibility, Zara fee engine, ISPPr document
payloads, vendor adapter requests, MinIO presigned links, OIDC
introspection/token flows.

| Threat | Mitigations (anchor) | Residual |
|---|---|---|
| **S**poofing — impersonating MPEx/ISPPr/vendor endpoint | OIDC client-credentials service auth; TLS on every hop (INV-5); vendor creds resolved per-call from OpenBao KV `vendors/{tenant}/{kind}` — never config/DB/repo | Cert pinning absent — standard TLS trust; accepted for stub seam, revisit at real-vendor selection (close-out ticket) |
| **T**ampering — mutating egress payload | TLS; manifest-bounded ISPPr payloads; deterministic vendor request hashing (external-id dedupe) | — |
| **R**epudiation | Outbox rows + audit record order placement/completion with actor; egress events logged field-classes only | — |
| **I**nformation disclosure — PII leaking to MPEx/Zara/vendor/logs | **PII firewall:** reflection CI test proves MPEx DTOs carry no `PiiClass` members (INV-2); `VendorEgress` per-kind allowlist enforced *exactly* (reflection test fails on drift — two-way match, PII fields tagged); ISPPr manifest-bounded; MinIO uploads classified + quarantined; masked-by-default on all read surfaces (INV-8) | Vendor stubs are deterministic stand-ins — real-vendor field contracts get re-reviewed at vendor selection (close-out ticket) |
| **D**enial of service — slow/hung downstream | Outbox pattern: outbound work queued, retried with backoff, `last_error` + warn logging, DLQ after N attempts; webhook channel closes outbox at submit so push vendors can't be re-polled | MPE-231: push-channel submitted-order timeout/reconciliation — submitted orders awaiting webhook have no expiry (ticketed) |
| **E**levation — SSRF via adapter endpoints | Adapter endpoints come from deployment config/OpenBao, never from request input; webhook callback URL is operator-set (`VENDOR_STUB_WEBHOOK_URL`), blank→poll (M13 fix) | — |

## 4. Logs & traces — observability surface

**Flows:** Seq structured logs, Jaeger spans, audit stream, `docker logs`,
CI artifacts.

| Threat | Mitigations (anchor) | Residual |
|---|---|---|
| **I**nformation disclosure — PII in logs/traces (the classic leak) | OTel/log redaction enforced at pipeline (INV-3, ADR-0009); audit events carry field *classes* never values (INV-4); CI test proves no-PII-in-logs; M13 Seq sweep (500 events) found zero SSN/name hits; M14 Phase 3 re-sweeps all services | Structured-log properties are the boundary — a future `logger.LogInformation("{User}", user)` slip is caught only by review + the CI test on known paths; no runtime content firewall on arbitrary log properties |
| **T**ampering — attacker scrubbing their trail | Audit is append-only hash-chained in the tenant file, independent of Seq retention (INV-4) | Seq/Jaeger themselves are dev-local authenticated services |
| **S**poofing / **E**levation via log sinks | Sinks receive, never call back; tokens not logged (BFF custody means raw tokens rarely enter the api log path at all) | — |

## 5. Deletion — data leaving existence

**Flows:** erasure requests, retention clock, backup expiry, crypto-shred.

| Threat | Mitigations (anchor) | Residual |
|---|---|---|
| **T**ampering — premature/forced deletion | Reg B/ECOA 25-month retention gate: erase inside window is refused; `force` requires admin + reason and writes `retention_override` audit (M6); shred = key destruction first, files second — recovery impossible by construction (INV-9) | Drill proof in Phase 2 |
| **I**nformation disclosure — "deleted" data still recoverable | Crypto-shredding: transit DEK destroyed → ciphertext unrecoverable even if file bytes persist; `secure_delete=ON` + `auto_vacuum=FULL` + WAL checkpoint discipline (INV-10); backups shredded with tenant; restore of a shredded tenant fails (drill-verified) | Host-level block-layer remnants below SQLite are environment scope |
| **R**epudiation — dispute over who erased | Erase path writes audit before destruction; control-plane `retention_override` event on forced erase | — |
| **D**enial of service — accidental key loss nuking tenant | Distinct from shred: accidental OpenBao transit-key loss = same unrecoverability; DR runbook documents key lifecycle + registry-backup recovery (Phase 2) | Accepted: erasure guarantee and disaster-recovery tension is inherent to crypto-shredding |

---

## Residual-risk register (all ticketed or accepted)

| # | Risk | Disposition |
|---|---|---|
| R1 | Push-channel submitted orders have no timeout/reconciliation | **MPE-231** (ticketed) |
| R2 | Webhook signature hex case-sensitivity could reject valid upstreams | **MPE-232** (ticketed) |
| R3 | Officer vocabulary in borrower REST projections | **MPE-225** (ticketed; denial enforced, cleanup pending) |
| R4 | npm dev-dep moderate vulns (jest/eslint `glob`/`minimatch`) — only fix is breaking jest@29 downgrade | Accepted: dev-tooling only, never ships; `--audit-level=high` gate enforced in CI |
| R5 | Dev-mode single-share unseal + co-located root token | Accepted for dev; production custody documented in DR runbook (Phase 2) |
| R6 | Real-vendor contract/credential review pending vendor selection | Close-out ticket (M14 Phase 5) |
| R7 | No WAF/managed edge in stack | Accepted for MVP topology; ingress hardening is rate-limit + body-bound layer |

## What this model does NOT cover

- Host/VM compromise beneath the encryption layers (kernel, fs snapshot
  custody) — environment assumption.
- Physical/datacenter security, CI-runner trust, dependency-build supply
  chain beyond the enforced vuln/secret gates.
- Insider threat beyond audit attribution (roles are coarse: admin/
  officer/borrower).
- Availability of sibling systems (MPEx/Zara/ISPPr) — outbox design makes
  their outage non-fatal to LOLA writes.
