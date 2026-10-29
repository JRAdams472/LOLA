# Data-Plane Runbook — tenant SQLCipher files

Operational guidance for the tenant data plane (M4): layout, filesystem
hardening, WAL/concurrency discipline, and the backup / restore /
erasure operator surface. Read alongside `docs/openbao-runbook.md`
(key operations) and `docs/adr/0003-sqlite-per-tenant.md`.

## Layout

| Path | Contents |
|---|---|
| `data/tenants/<tenant>.db` | live SQLCipher file (AES-256, per-tenant key) |
| `data/tenants/<tenant>.db-wal` / `-shm` | WAL companions — never copy or delete while live |
| `data/backups/<tenant>/<utc>.db` | `VACUUM INTO` snapshot, encrypted with the SAME tenant key, `journal_mode=DELETE` (single immutable artifact) |
| `data/backups/<tenant>/<utc>.db.manifest.json` | `{tenant, name, utc, sha256, bytes}` — verified before every restore |
| control-plane `tenants.db_key_wrapped` | the file key as `vault:` transit ciphertext — never plaintext at rest |

Keys live in OpenBao transit (`tenant-<id>` DEK + `tenant-<id>-idx`
blind index). Plaintext file keys exist only in memory for the life of
an operation.

## Filesystem ACLs

- `data/` trees must be `0700` dir / `0600` files, owned by the service
  account only — defense in depth under encryption, not instead of it.
- The API process runs as a dedicated unprivileged user; only that user
  reads `data/tenants` and `data/backups`.
- In containers, bind-mount `data/` as a dedicated named volume (see
  `docker-compose.yml`); never bake tenant files into an image, never
  place them on a shared/host-writable mount.
- Backup consumers (off-box replication, archive pulls) should read
  `data/backups/<tenant>/` read-only — snapshots are already encrypted,
  so they are safe to ship off-box verbatim; the manifest travels with
  them.

## Concurrency discipline (WAL, single writer)

- SQLite is **single-writer** per file. WAL allows concurrent readers
  plus one writer; long-running transactions still block writers.
- `TenantConnectionFactory` opens a fresh keyed connection per use with
  **pooling OFF** — a pooled physical connection would hold the tenant
  handle AND its plaintext key across requests. Do not re-enable pooling
  without an explicit key-lifecycle design (see the factory's comment).
- One process should own writes to a tenant file. Running a second API
  replica against the same `data/tenants` volume is unsupported.
- Restore (`POST .../restore`) checkpoints the live file
  (`wal_checkpoint(TRUNCATE)`) before swapping; it is still an operator
  action — do it during a write-quiet window for that tenant. A
  `SQLITE_BUSY` during restore aborts before the swap.
- Never hand-copy a live `<tenant>.db` — a hot file with a populated
  `-wal` is incomplete. Use the backup endpoint (VACUUM INTO is
  online-consistent) or checkpoint first.

## Backup / restore ops

```bash
POST /api/admin/tenants/{id}/backup          # snapshot + manifest
GET  /api/admin/tenants/{id}/backups         # manifest list, newest first
POST /api/admin/tenants/{id}/restore         # body: {"name": "<utc>.db"}
```

All three are Provision-policy (operator) routes. Snapshot names are
flat `<utc>.db` files inside `backups/<tenant>/` — the restore gate
rejects anything that isn't a listed manifest name, including
traversal-shaped values.

Restore order of operations (all enforced in `TenantBackupService`):

1. sha256 of the snapshot vs its manifest — tampered/corrupt snapshots
   are refused before any bytes move.
2. `PRAGMA integrity_check` over a **read-only** keyed connection — the
   wrong key dies here as `file is not a database`; a `Mode=ReadOnly`
   probe provably cannot mutate the verified artifact.
3. Live file `wal_checkpoint(TRUNCATE)`, stale `-wal`/`-shm` removed.
4. File swap, then a keyed `integrity_check` on the restored file.

Why snapshots are `journal_mode=DELETE`: a WAL-mode SQLCipher file is
never byte-stable — every keyed open+close checkpoints and re-encrypts
pages — so a sha256 manifest is only meaningful over a delete-mode
artifact. The live file returns to WAL automatically: every keyed open
re-asserts `journal_mode=WAL`.

Off-box backups: ship `<utc>.db` + `<utc>.db.manifest.json` together.
Snapshots are useless without the tenant's transit key — losing the key
loses the backups too; that is the intended crypto-shred property.

## Erasure ops

```bash
DELETE /api/admin/tenants/{id}               # Provision policy
DELETE /api/admin/tenants/{id}?force=true&reason=...   # retention override
```

`TenantErasureService` destroys keys **before** files (invariant 9):

1. `ITenantKeyManager.DestroyAsync` — two-step transit delete of BOTH
   `tenant-<id>` and `tenant-<id>-idx`. After this returns, every
   tenant ciphertext, blind index, wrapped file key, AND every snapshot
   in `backups/<tenant>/` is permanently unrecoverable.
2. Deletes `data/tenants/<id>.db` (+ `-wal`/`-shm`) and
   `data/backups/<id>/`.
3. Registry status → `retired`.

Ordering rationale: if a crash lands mid-erasure, whatever survives on
disk is ciphertext with destroyed keys — deletion is cleanup, not the
security boundary. Erasure is refused for already-`retired` tenants
(400) and unknown ids (404).

Retry-safety (MPE-107): re-running an erase that died mid-way is safe —
already-destroyed keys read as absent (not an error), file cleanup is
best-effort, and the tenant always converges to `retired`. Anything
that failed to delete is reported in `unresolvedPaths` for an operator
sweep; the bytes are unreadable once keys are gone either way.

**Retention gate (MPE-118):** Reg B/ECOA requires application records
be kept 25 months. Erasure crypto-shreds every application in the file,
so `EraseTenantHandler` computes each app's `retention_until =
created_utc + 25 months` and the tenant's earliest-clear date (the last
window to close) BEFORE keys die:

- Any app inside its window → **409** carrying the earliest-clear date;
  nothing is touched.
- `?force=true&reason=<required>` overrides the window: the erase
  proceeds, and a `retention_override` event is written to the
  **control-plane** `audit_events` table first — the in-file audit log
  is shredded with the data it describes, so the only durable record
  of the override lives outside the tenant file.
- An empty tenant (no applications) has no retention barrier.
- Per-application disposal is report-only for now — see
  `GET /api/tenants/{id}/retention` for per-app windows and the
  tenant's earliest-clear date.

## Failure notes

- `file is not a database` on open → wrong/missing key or non-SQLCipher
  file; check the registry's `db_key_wrapped` and the transit key.
- `tenant file is busy` on restore → the live checkpoint could not
  fully drain (a reader/writer holds a snapshot); the swap never
  started — retry in a quiet window.
- A tenant left `provisioning` after a failed provision is resumable —
  re-run `POST /api/admin/tenants`; the pipeline resumes at the failed
  step (see `docs/identity-runbook.md`).
