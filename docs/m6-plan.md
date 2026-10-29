# M6 Implementation Plan — Audit, masking layer, PII data-map & retention

Parent: **MPE-70** — conforms to ADR-0003 (per-tenant file), ADR-0004
(OpenBao/crypto-shred), ADR-0008 (masked-by-default; reveal is the
explicit audited action), ADR-0009 (field classes in audit, never
values), ADR-0010 (coverage), ADR-0011 (REST + GraphQL same handlers),
invariants 3/4/9.

User decisions locked at planning:

- **Retention gate:** tenant erasure *blocked* while any application is
  inside its Reg B 25-month window; explicit `force` + `reason`
  override bypasses and writes a **control-plane** audit event (an
  in-file audit row would be shredded with the data).
- **Per-application disposal:** report-only — the engine flags/
  computes eligibility; disposal stays tenant-level crypto-shred.
- **Reveal:** `POST .../applications/{id}/reveal {reason}` — explicit
  action endpoint, unmasked doc, `reveal` audit event.
- **MPE-108 residuals:** delete invites/OIDC tokens/`svc-{tenant}`/
  groups; anonymize `users`; keep `tenants` retired +
  `tenant_migrations` as the audit spine.
- **MPE-91** (platform-operator identity) → folded into **M7** planning.

## Core design

`audit_events` (tenant file `0004_audit.sql`; control plane
`0005_audit.sql`):

```sql
seq INTEGER PRIMARY KEY AUTOINCREMENT,
event_id TEXT NOT NULL, actor TEXT NOT NULL, action TEXT NOT NULL,
entity TEXT NOT NULL, entity_id TEXT,
field_classes TEXT NOT NULL,          -- JSON array of PiiClass names
reason TEXT, prev_hash TEXT NOT NULL, hash TEXT NOT NULL,
created_utc TEXT NOT NULL DEFAULT (...)
```

- `hash = SHA-256(prev_hash | actor | action | entity | entity_id |
  field_classes | reason | created_utc)`; genesis `prev_hash = "GENESIS"`.
  Insert reads last hash on the same connection — single-writer file,
  no race.
- `IAuditLog` port: `WriteAsync`, `VerifyChainAsync` (first-broken seq),
  `QueryAsync(filters)`. `TenantAuditLog` via `TenantConnectionFactory`;
  `ControlPlaneAuditLog` via `ControlPlaneDb`; shared `HashChain`.
- **Emission at handler level** (actor + reason live there): inject
  `IAuditLog` into Get/UpdateDraft/Submit/Correct/Import/Search/
  Export/Reveal handlers; `field_classes` = union of catalog PII
  classes touched. Control-plane emission on identity + admin surfaces
  (override, erasure, anonymization).

## Phases

### Phase 1 (MPE-116) — hash-chained audit engine — ADR-0003/0009

Both `audit_events` tables; `src/Lola.Application/Audit/` (port,
event, `HashChain`); `TenantAuditLog` + `ControlPlaneAuditLog`; handler
emission; `GET .../applications/audit/verify` → `{ok, firstBrokenSeq}`.
**Proof:** real OpenBao+SQLCipher — events chain-verify; raw row flip
→ verify reports the broken seq.

### Phase 2 (MPE-117) — audited reveal + access-review — ADR-0008/0009/0011

`POST .../applications/{id}/reveal {reason}` (`RevealPii` policy —
TenantAdmin/LoanOfficer; borrower 403) returns unmasked doc + `reveal`
event. `GET .../applications/{id}/audit` + `GET .../tenants/{id}/audit`
staff-only access-review (who read what PII, when). Control-plane
emission on identity endpoints.
**Proof:** reveal returns plaintext + event; borrower 403; masked GET
unchanged; report lists actor/action/classes/utc.

### Phase 3 (MPE-118) — data-map + retention engine — ADR-0003/0006/0009

`docs/pii-data-map.json` generated from the field catalog + `[PiiClass]`
reflection + drift-check test. `RetentionPolicy` computes
`retention_until = created_utc + 25mo` per app; `GET .../tenants/{id}/
retention` report. `TenantErasureService` gate: inside-window refuses
with the retention date unless `?force=true&reason=` → control-plane
`retention_override` event. Runbook update. Injectable `TimeProvider`
for tests.
**Proof:** policy-math unit tests; fresh tenant refused; backdated/
clock-shifted app → erase allowed; override audits in control plane.

### Phase 4 (MPE-119) — MPE-108 residuals + marquee + gate — ADR-0003/0004/0008

Post-`retired` lifecycle: delete invites + OIDC tokens/authorizations +
`svc-{tenant}` + groups/memberships; anonymize `users` (email→
`erased-{id}@erased`, display_name→`erased`, password_hash→NULL,
status→disabled); keep `tenants` + `tenant_migrations` as the spine.
Runbook contract. **Marquee:** provision → submit → masked read vs
reveal → blind-search event → chain verify → raw flip fails →
retention gate → override → erase → residuals gone + spine retained.
**M6 hardening gate** comment on MPE-70; findings → tickets.

## Risks

- Handler-level audit emission can miss a future handler — convention
  comment + gate checklist.
- `erased-{id}@erased` preserves the `users(tenant_id,email)` UNIQUE.
- Retention clock = `applications.created_utc` per ticket; Reg B's
  action-taken clock is a runbook-noted refinement candidate.
