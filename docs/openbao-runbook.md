# OpenBao Runbook — LOLA

Key management for field-level PII encryption (M2+). OpenBao's **transit**
engine does all crypto — keys never leave the service; the API only ever
sends plaintext↔ciphertext pairs over the wire.

## Layout

| Piece | Where | Notes |
|---|---|---|
| Mount | `lola-transit` | transit engine — encrypt/decrypt/rewrap/hmac |
| Tenant DEK | `lola-transit/keys/tenant-<id>` | aes256-gcm96 — randomized ciphertext |
| Tenant index key | `lola-transit/keys/tenant-<id>-idx` | hmac — blind indexes |
| API token | `data/openbao/api-token` | `lola-api` policy — transit only, never root |
| Unseal key / root token | `data/openbao/{unseal-key,root-token}` | dev only — gitignored, mode 600 |

## Dev stack (compose)

`docker compose up` runs `openbao` (file backend, `openbao-data` volume)
plus a one-shot `openbao-init` that:

1. `operator init` (1-of-1 dev share — **dev only**) → unseal key + root
   token to `data/openbao/` (idempotent: skipped when already
   initialized)
2. `operator unseal`
3. Mounts `lola-transit`, writes the `lola-api` policy, mints the api
   token

On restart the volume still holds the initialized barrier — init just
re-unseals. Full teardown (`compose down -v` or deleting
`data/openbao/`) ⇒ **all tenant keys are gone and every ciphertext in
dev is permanently unrecoverable**. That is crypto-shredding working as
designed — do it only intentionally.

Sealed openbao ⇒ `bao status` non-zero ⇒ compose healthcheck fails ⇒ api
waits on `openbao-init`. The fix is always re-run unseal, never a
restart loop.

## Sealed-state ops for real deployments (not dev)

Dev's single unseal share in a local file exists purely so `compose up`
stays zero-touch. Anything real must:

- `operator init -key-shares=5 -key-threshold=3` (or org policy) —
  shares distributed to separate key holders, never stored with the
  service
- TLS on the listener (`listener "tcp" { tls_cert_file/tls_key_file }`)
  — dev's `tls_disable` is a compose-only carve-out
- Auto-unseal via a KMS/HSM stanza, or a documented human unseal
  ceremony; no plaintext share files
- Audit devices enabled (`bao audit enable file ...`) — transit
  operations are PII-relevant events
- Token: short-TTL service auth (approle/jwt) with renewal, not a
  long-lived static token in a file

## Key rotation (per tenant)

```bash
bao write -f lola-transit/keys/tenant-<id>/rotate
```

Old ciphertext keeps decrypting (versioned keys). New writes use the
latest version. To push existing ciphertext forward without exposing
plaintext to the API:

```bash
bao write lola-transit/rewrap/tenant-<id> ciphertext=vault:v1:...
# → returns vault:v<new>:... — update the column with the new value
```

Rewrap runs inside OpenBao; plaintext never transits the wire.
`TransitKeyManager` (M2 Phase 2) exposes Rotate/Rewrap primitives; the
sweep over stored rows is an ops/job concern layered on top.

## Index-key rotation ⇒ reindex

Blind indexes are HMACs under `tenant-<id>-idx`, stored as
`idx:v<n>:<hmac>`. Rotating that key changes future index values, so
existing rows won't match new lookups. Procedure:

1. Rotate `tenant-<id>-idx`
2. Recompute every `*_idx` column from the (decrypted) field value —
   needs a decrypt+re-hmac sweep; schedule it, it's not instant
3. Until the sweep finishes, equality search on that field is degraded —
   plan rotations for low-traffic windows

## Crypto-shredding (erasure primitive)

Transit refuses key deletion until `deletion_allowed` is set — the
two-step is deliberate, shredding can never be a fat-fingered single
call:

```bash
bao write lola-transit/keys/tenant-<id>/config deletion_allowed=true
bao delete lola-transit/keys/tenant-<id>
bao write lola-transit/keys/tenant-<id>-idx/config deletion_allowed=true
bao delete lola-transit/keys/tenant-<id>-idx
```

Every field encrypted under the tenant DEK and every blind index becomes
permanently unrecoverable — decryption fails even if the database file
survives. Retention obligations (Reg B 25-month) are enforced by the
caller *before* shredding — the key delete itself is unconditional.

## Disaster recovery

Drill-proven (MPE-234, 2026-11-19 dev stack): every statement below was
executed against the compose deployment, not asserted from docs.

### Sealed vault — behavior and recovery

While sealed, every transit call returns `503 Vault is sealed`. The API
fails **closed and loud**: tenant provisioning aborts mid-pipeline
(`OpenBao 503`), crypto-dependent handlers error, and background drains
log failures and retry — nothing silently corrupts or skips crypto. The
registry stays readable (plain SQLite) so read-only admin ops still
answer.

```bash
# confirm sealed
curl -s $OPENBAO_ADDR/v1/sys/seal-status   # {"sealed":true,...}
# unseal (dev — single share)
curl -s -X PUT $OPENBAO_ADDR/v1/sys/unseal \
  -d "{\"key\":\"$(cat data/openbao/unseal-key)\"}"
# prod — threshold shares, one call per holder:
bao operator unseal <share>   # repeat until sealed:false
```

After unseal the API recovers with no restart — transit ops resume on
the next request. A tenant that died mid-provisioning sits at status
`provisioning`; either resume it (`POST /api/admin/tenants` re-runs the
pipeline from the failed step) or erase it — both paths handle the
no-data-plane-key state (MPE-234).

### Vault data loss (raft/file backend destroyed)

Losing the OpenBao data volume loses every tenant DEK + index key —
**equivalent to shredding every tenant at once**; no ciphertext in any
tenant file or backup is recoverable. This is the encryption design
working, not a bug: OpenBao *is* the key custodian.

Recovery sequence after catastrophic vault loss:

1. Re-init the vault (`openbao-init` in dev; `operator init` +
   re-mount `lola-transit` + re-write `lola-api` policy + re-mint the
   api token in real deployments).
2. Tenant data is gone with the keys — re-provision tenants and restore
   from *external* data sources if any exist. `data/backups/` snapshots
   are useless without the transit keys (intended crypto-shred
   property — see data-plane runbook).
3. Rotate the api token path: the old token is dead with the storage;
   re-mint per init.

**If backups must survive a vault loss**, custody the tenant keys out
of band (e.g., `bao read lola-transit/keys/tenant-<id>` export under
org key-custody policy) — a deliberate key-backup strategy is the only
DR lever that keeps `VACUUM INTO` snapshots meaningful. Dev does not
do this.

### Transit-key loss (per tenant)

`tenant-<id>` DEK deleted/lost ⇒ all PII ciphertext + the wrapped file
key for that tenant are permanently unrecoverable — the file and its
backups become random bytes. `tenant-<id>-idx` lost ⇒ blind indexes
die with it (equality search breaks even if fields decrypt — they
don't, both keys go together in practice). Key loss is indistinguishable
from erasure by design; protect keys accordingly.

### Root token revocation / rotation

The root token can do anything, including `sys/seal` and policy writes.
Dev's file-stored root (`data/openbao/root-token`, gitignored) is a
dev-only convenience. For real deployments:

```bash
bao token create -policy=root -ttl=8h     # bounded operator token
bao token revoke <old-root-accessor>      # revoke the old one
```

Prefer day-to-day ops on scoped tokens (`lola-api` policy) and revoke
root between maintenance windows. Audit device output (prod
requirement, above) is how you detect token misuse after the fact.
