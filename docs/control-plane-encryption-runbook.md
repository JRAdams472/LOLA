# Control-plane encryption — conversion & recovery runbook (MPE-276/287)

Applies ADR-0002/0003/0004. The conversion is **offline and
operator-run** — there is no web-request migration path and no
plaintext temp export at any step.

## 1. Convert a legacy (unkeyed) control.db

```bash
# 0. STOP WRITERS: scale the api to 0 (or maintenance-mode the host).
docker compose stop api

# 1. Fresh destination key (generate once, store as a wrapped vault:
#    blob under data/openbao/ or your secret store — never in repo).
openssl rand -hex 32            # 64 hex chars = the new file key

# 2. Run the tool. OPENBAO_TOKEN must have transit encrypt/decrypt/hmac
#    on lola-transit for `control-plane` + `control-plane-idx` keys.
OPENBAO_ADDR=http://localhost:8300 \
OPENBAO_TOKEN=$(cat data/openbao/api-token) \
dotnet run --project tools/cpconvert -- \
  data/control.db data/control-keyed.db --key <64-hex|@keyfile>
```

The tool:
- `PRAGMA wal_checkpoint(TRUNCATE)` on the source, then copies it to
  `control.db.cpbak` (protected custody backup — see §4).
- Creates `control-keyed.db` keyed on `--key` and runs all migrations.
- Streams every table, encrypting classified columns and computing
  `*_idx` blind indexes (audit rows copy **verbatim** — the hash chain
  is never rewritten, only re-verified).
- Writes `cpconvert_state` per committed table — a killed run resumes,
  never restarts committed tables.
- Validates: per-table row counts, `pragma_foreign_key_check`, audit
  chain re-verification, full decrypt round-trip. Any failure aborts —
  no partial switch.
- **Never swaps files.** On `VALIDATED`, switch manually:

```bash
# 3. Atomic switch (POSIX rename is atomic on the same filesystem).
mv data/control.db data/control.db.legacy
mv data/control-keyed.db data/control.db
# 4. Point CONTROL_DB_KEY(_FILE) at the new key, restart writers.
docker compose start api
# 5. Smoke: login with org hint, /connect/authorize, health endpoint.
```

## 2. Rollback contract

- **Before writers reopen**: delete `control-keyed.db`, keep
  `control.db` (the `.cpbak` is a second protected copy). Nothing else
  needed — the keyed file never served a request.
- **After writers reopen**: rollback is a *reconciled recovery* —
  replay the post-switch writes into the legacy file by hand-review.
  **Never** swap a stale file back over a live one; that silently
  drops post-switch grants/invites/audit rows (invariant 4).

## 3. Rotation / rekey

- **File key**: `PRAGMA rekey = "x'<newhex>'"` on a keyed open (offline,
  writers stopped), then rewrap: transit-encrypt the new hex under
  `lola-transit/keys/control-plane` and replace `data/openbao/control-db-key`.
  Proven in `CpConvertTests.Rekey_round_trip_*`.
- **Transit field key**: OpenBao key rotation (`lola-transit/keys/<name>/rotate`)
  + `rewrap` job — ciphertext stays under key version N until rewrapped;
  transit decrypt is version-transparent.

## 4. Legacy custody + approved disposal

Conversion does **not** retro-protect existing artifacts. Until
disposal is approved, these stay under restricted custody:

| Artifact | Path | Action |
|---|---|---|
| Legacy plaintext DB | `data/control.db`, `data/control.db.legacy` | keep until post-conversion soak, then secure-delete |
| Custody backup | `data/control.db.cpbak` | same retention as above |
| WAL/SHM | `data/control.db-wal`, `-shm` | deleted by the checkpoint step; delete any strays |
| Backups/images | `backups/control*`, host/volume snapshots, CI artifacts | inventory before conversion; expire per retention policy |
| Old file key | env/secret-store copies | revoke after rekey soak |

Disposal = secure erase (overwrite + delete) once the keyed file has
served a full business cycle without rollback need. Record the
disposal date in the ops log.

## 5. Failure modes (rehearsed in `CpConvertTests`)

- Unkeyed/wrong-key open of a keyed file → fails at `Open()` (forced
  page-1 read). A plaintext-header file + configured key → actionable
  "run cpconvert" error.
- Killed mid-table → `cpconvert_state` resumes committed tables only.
- Duplicate `(tenant,email)` after normalization → UNIQUE on
  `email_idx` fails; no partial switch.
- OpenBao down → transit calls fail; the tool aborts (no plaintext
  fallback), API health reports not-ready.
- Erased-tenant restore: tenant transit keys are destroyed before
  residual cleanup; a restored tenant backup carries ciphertext that
  cannot decrypt — identity fields stay dead. (Residual risk: a full
  *key-service* backup restoring the transit keys — owned by
  key-backup retention policy, documented in design §7.)
