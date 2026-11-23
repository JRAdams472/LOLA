# Control-plane encryption — design (MPE-276 / remediation Phase 7)

Status: **design for approval** — approval gate per phase-7 contract;
no code lands until the design + ADR amendments are approved.
Scope: LOLA only, before real data. ADR-0002/0003/0004/0005.

## 1. Inventory + classification

Two layers protect the control plane, same as the data plane:
**file-level SQLCipher key** (new — the file is plaintext today) and
**field-level transit encryption** for identity-class columns.

| Table.column | Contents | Class |
|---|---|---|
| users.id | opaque user id | retain (opaque ref) |
| users.tenant_id | opaque tenant ref | retain |
| users.email | auth identifier | **encrypt + keyed normalized blind index** |
| users.display_name | identity | **encrypt** |
| users.org_name | partner org attribution | **encrypt** |
| users.password_hash | Argon2id PHC | retain (non-reversible hash — never indexed) |
| users.status / mfa_enabled / auth_version / timestamps | lifecycle ints/enums | retain |
| users.mfa_secret_ref | `vault:` ref only | retain (opaque ref, not a secret) |
| invites.email / org_name | pre-identity PII | **encrypt + blind index / encrypt** |
| invites.token_hash | SHA-256 | retain (hash-only) |
| invites.role / created_by / timestamps | enums + opaque ids | retain |
| tenants.id / slug | routing identity | id retain; slug **encrypt + blind index** (login discovery keys on it) |
| tenants.display_name | identity | **encrypt** |
| tenants.status / db_path / timestamps | lifecycle + local path | retain |
| tenants.db_key_wrapped / isppr_key_wrapped | transit ciphertext | retain (already wrapped) |
| tenants.isppr_client_id / customer_id / key_id | vendor refs | retain (opaque refs — classify, revisit if a vendor makes them identifying) |
| groups.name / description | incl. custom names | name **encrypt + blind index** (unique per tenant); description **encrypt** |
| groups.system_kind / *_roles / user_groups | structural + opaque ids | retain |
| role_visibility | enum map | retain |
| openiddict_applications.* | client registry; `svc-{tenant}` ids embed tenant id | client_id → **blind index** (UNIQUE lookup); secrets already OpenIddict-hashed; JSON config retain |
| openiddict_authorizations.subject / properties | user id ref + claims JSON | subject retain (opaque id); properties **encrypt** (may carry claim values) |
| openiddict_tokens.subject / payload / properties / reference_id | grant rows | subject retain; payload retain (already OpenIddict-encrypted or ref id); properties **encrypt**; reference_id retain (opaque, UNIQUE) |
| openiddict_scopes.* | names | retain |
| audit_events.* | hash-chained spine | retain as-is. `actor` is an opaque id; **historical rows can never be rewritten** (chain) — documented exception; verify no email ever lands in `actor`/`entity_id`/`reason` going forward (reason codes already enforced, MPE-274) |
| tenant_migrations / tasks | rollout state + task queue (system titles, opaque ids) | retain; `tasks.claimed_by` is an opaque user id |
| invites (legacy pre-v2), WAL, -shm, backups under `data/` | plaintext copies today | covered by file key going forward; **legacy files get custody/disposal tracking — new encryption does not retro-protect them** |

No column is removed; `db_path` is a local filesystem path (ops
metadata, not identity) — retained, documented.

## 2. File-key bootstrap (independent, fail-closed)

- New dedicated transit key `transit/keys/control-plane` in OpenBao —
  its own key domain, never a tenant key and never a row inside the
  locked DB.
- The file key is a random 32-byte key generated once (conversion
  tooling), wrapped by `control-plane` transit, and provisioned to the
  runtime as `CONTROL_DB_KEY` = the wrapped `vault:v1:…` blob (env /
  mounted secret / workload identity per deploy).
- `ControlPlaneDb.Open()` resolves: unwrap `CONTROL_DB_KEY` via the
  control-plane transit client → `PRAGMA key` → `cipher_compatibility`
  check → **then** any read. Missing/invalid bootstrap ⇒ open fails
  (ties into Phase 8's production fail-closed profile; dev compose
  provisions via `openbao-init` like today).
- Rotation: `PRAGMA rekey` (file key) + transit rewrap of the wrapped
  blob — both offline operations, documented in the runbook.
- OpenBao outage ⇒ control-plane file cannot open ⇒ API reports
  not-ready (separate liveness/readiness per Phase 8). Dev fallback:
  `CONTROL_DB_KEY` may be a file-mounted raw key in Development only.

## 3. Field encryption + keyed blind indexes

- Reuse `IFieldCipher`/blind-index machinery with a **control-plane
  key domain**: `transit/keys/control-plane` for global rows.
  **Tenant-identity fields use the tenant's own transit key**
  (`tenant-<id>`) per the phase text — erasure of a tenant's keys
  automatically crypto-shreds its control-plane ciphertext (users,
  invites, org names) even before residual cleanup runs.
- Blind index = HMAC keyed by the same key domain over a normalized
  value (lower-case/trim for email, slug). Indexes:
  `users(tenant_id, email_idx)` UNIQUE replaces
  `ux_users_tenant_email`; `invites(email_idx)`;
  `tenants.slug_idx` UNIQUE; `groups(tenant_id, name_idx)` UNIQUE.
  Normalized equality works; no global/unsalted index.
- Password and invite-token hashes stay hashes (non-reversible by
  design — Argon2 PHC / SHA-256).
- Unauthorized/absent lookups return the same generic "not found /
  invalid credentials" — no oracle on encrypted-vs-absent.

## 4. Tenant-aware login discovery (before plaintext lookup is removed)

Today `FindByEmailAsync` does a global lower(email) scan — impossible
once email is ciphertext + tenant-scoped index. Contract: the caller
must know the tenant before lookup.

- Login form gains a **tenant hint**: the dev form adds an `org`
  field (tenant slug). `FindByEmailInTenantAsync(tenant_id, email)`
  already exists — it becomes the only email path, resolved via
  `slug_idx → tenant_id`.
- Portal invite-accept flow already carries tenant context; officer
  console resolves tenant from the typed slug.
- Ambiguity (same email, two tenants) is resolved by the explicit
  hint — never by scanning. Missing/unknown slug = generic failure.

## 5. Keyed consumers

Everything that touches `control.db` opens through the keyed path —
`ControlPlaneDb.Open()` is already the single seam for: API requests,
`ControlPlaneMigrator` (DbUp — apply key before journal read),
`Healthy()`, all four OpenIddict Dapper stores, seeders, and tools
(`InvalidRoleGrantScan`, residuals, backup). Migrations 0011+ add the
`_idx` columns and indexes **before** writers reopen; backfill of
ciphertext + indexes is the offline conversion tool, never a web
request.

## 6. Offline conversion (resumable, disposable-fixture proven)

Tool: `tools/cpconvert` (console entry, not a web path):

1. Stop writers (operator step — documented, not enforced by tool).
2. `PRAGMA wal_checkpoint(TRUNCATE)`; take a protected copy backup.
3. Open source unkeyed, open **separate keyed destination** (new file
   key); replay schema (migrated form), transform row-by-row:
   encrypt classified fields, compute blind indexes, copy opaque
   rows verbatim — **no plaintext temp export at any point**.
4. Verify: row counts per table, FK integrity, unique constraints
   (esp. tenant-scoped email/name/slug dupes), OIDC store
   round-trip, audit chain re-verification (hashes recomputed —
   chain is preserved, not rewritten).
5. Resumable: destination carries a `cpconvert_state` table;
   re-run continues per-table. Switch = atomic file swap only after
   validation.
6. Rollback: **before** new writes, restore the protected backup;
   after writes reopen, rollback is a reconciled recovery (replay
   deltas), never a stale-file swap. Documented in runbook.
7. Existing user databases are NOT converted by this work —
   operator runs the tool explicitly later.

## 7. Legacy custody + rehearsal

- Inventory existing `data/control.db*`, `-wal/-shm`, backups and any
  copies — restricted custody until approved disposal; new encryption
  does not retroactively protect them.
- Rehearse on disposable fixtures: keyed open fails unkeyed/wrong-key;
  raw SQL on keyed file shows ciphertext; same-tenant normalized email
  lookup + dup constraint; same email × 2 tenants unambiguous; WAL /
  VACUUM INTO snapshot / logs scanned for marker plaintext; OpenBao
  outage → not-ready; interrupted conversion resumes; backup restore
  of a live tenant works; **restored backup of an erased tenant does
  NOT resurrect identity fields** (keys destroyed = ciphertext dead;
  an old key-service backup restoring transit keys is a documented
  residual risk owned by key-backup retention policy).

## 8. ADR amendments needed

- **ADR-0004** (OpenBao envelope): extend scope to the control-plane
  file + control-plane key domain; record file-key bootstrap and
  tenant-keyed identity fields.
- **ADR-0005** (OpenIddict identity): tenant-aware login discovery
  (slug hint) replacing global email lookup.
- No decision reversals; both are extensions. **Approval required
  before code per contract.**

## 9. Proposed subticket split (dependent)

- **P7a** — design doc + ADR amendments (this doc; approval gate).
- **P7b** — keyed open + file-key bootstrap + schema migration
  (0011: `_idx` columns/indexes) + greenfield keyed provisioning.
- **P7c** — field encryption + blind indexes in directories/stores,
  tenant-aware login, encrypted OIDC `properties`.
- **P7d** — offline conversion tool + runbook + rehearsal suite
  (interruption, outage, rotation, restore, erasure) + legacy-custody
  note. Close-out.
