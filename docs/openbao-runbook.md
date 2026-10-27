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
