# LOLA Deployment Runbook (MPE-277)

Production startup is **fail-closed**: `ProductionProfile.Validate`
(`src/Lola.Api/Hosting/ProductionProfile.cs`) runs before the host
serves anything and throws — listing every missing/stub/insecure
setting by *name, never value* — unless the whole profile is safe.
Both BFFs gate equivalently via `assertSessionEnv`
(`clients/{web,portal}/lib/session-env.ts`). If the API won't boot, the
boot log's violation list is the fix list — don't bypass it.

## Required environment

Everything in `.env.example`'s "PRODUCTION PROFILE" block. The core
contract:

| Area | Required |
|---|---|
| Control plane | `CONTROL_DB_KEY`/`_FILE` — wrapped `vault:v1:` or raw |
| OpenBao | `OPENBAO_ADDR` (https), `OPENBAO_TOKEN`/`_FILE` (runtime scope) |
| OIDC keys | `OIDC_{SIGNING,ENCRYPTION}_KEY`/`_FILE` — PEM, RSAParameters JSON, or `vault:v1:` blob under transit keys `oidc-signing`/`oidc-encryption`. **Never generated** — a boot that creates keys is a misconfiguration |
| OIDC URLs | `OIDC_ISSUER` (canonical https), `OIDC_*_CALLBACK_URI`, `OIDC_*_POST_LOGOUT_URI` — all https, all matching what clients register |
| Seed secrets | `OIDC_SEED_SVC_SECRET`, `OIDC_SEED_OPS_SECRET` — ≥16 chars, distinct, never a dev value |
| DataProtection | `DP_KEY_DIR` — persistent, access-controlled key-ring dir |
| Adapters | `PRICING/FEE/DOC/VAULT/VENDOR_ADAPTER` = a real selection or `disabled`; enabled adapters need their full egress config |
| Transport | Every enabled egress https. Loopback-only http requires the env var named in `INSECURE_ENDPOINT_ALLOWLIST` |
| BFFs | `BFF_CLIENT_ID` (`lola-bff-*`) + `BFF_CLIENT_SECRET` (real secret matching the API's `OIDC_BFF_*_SECRET`), `SESSION_SECURE=1`, all OIDC/`API_URL`s https. `ALLOW_INSECURE_SESSION_ENV` exists only for the dev compose stack |

Forbidden in production: `DEV_SEED_*`, `VENDOR_STUB_WEBHOOK_URL`,
`DOC_CLASSIFIER=stub`, wildcard `TRUSTED_PROXIES`, `stub`/unset
adapters.

## Secrets and tokens — who needs what

- **Runtime `lola-api` token** (OpenBao): scoped — transit encrypt/
  decrypt on `control-plane`, `oidc-signing`, `oidc-encryption`, plus
  tenant data keys, KV read for vendor creds. This is the *only* token
  the API holds. Least privilege: if a capability isn't used, don't
  grant it.
- **Migration/cpconvert credentials** (`tools/cpconvert`,
  `control-plane-encryption-runbook.md`): separate, higher-privilege,
  used by operators only — never in the service env.
- **Root/unseal material**: never at runtime, never in the repo,
  never in the service env. OpenBao root token and unseal shares live
  in the offline key ceremony store.
- **BFF session-store credentials** (`BFF_CLIENT_SECRET` per app, matched
  by the API's `OIDC_BFF_CONSOLE_SECRET`/`OIDC_BFF_PORTAL_SECRET`):
  independent per app — the console client and portal client are distinct
  (their `lola_bff_ns` claims pin each to its own session namespace).
- Rotation of any secret: roll the env/file, restart. Every reader
  resolves at boot (`OidcKeyMaterial`, `ControlPlaneKeyResolver`,
  `OpenBaoOptions`) — no hot reload, no stale cache.

## Key rotation

- **OIDC signing**: provision the new key, set `OIDC_SIGNING_KEY*`,
  restart. Outstanding access tokens minted under the old key fail
  validation immediately — schedule with a maintenance window or keep
  the old key as a second `AddSigningKey` until the longest token TTL
  elapses. Encryption key rotation invalidates outstanding refresh/
  session material — rotate during a window.
- **Control-plane key**: `docs/control-plane-encryption-runbook.md`
  owns the wrap/unwrap ceremony (`cpconvert`); the runtime only reads
  `CONTROL_DB_KEY`.
- **OpenBao transit keys**: transit rotates internally; wrapped blobs
  (`vault:v1:`/`vault:v2:`) remain decryptable — no app change.
- **Certs**: TLS certs for ingress/egress rotate per the platform's
  cert manager; the API validates `https://` scheme at boot — a cert
  outage shows as readiness failure, not a boot failure.

## Readiness vs liveness

- `/healthz` — liveness only: process is up. No dependencies; a
  sealed vault or dead DB never flips it (restart loops would).
- `/readyz` — 200 only when ready to serve: control DB reachable,
  migrations current, OpenBao `unsealed` in production, ClamAV
  reachable when `VAULT_ADAPTER=minio`. 503 body names the failing
  components (`controlDb`, `migrations`, `openbao`, `scanner`),
  never why or with what credential.
- Point the orchestrator's readiness gate at `/readyz`, liveness at
  `/healthz`.

## Outage recovery

- **Sealed OpenBao**: `/readyz` → `openbao:sealed`, logins and transit
  ops fail. Unseal per `openbao-runbook.md`; readiness recovers on the
  next probe — no restart needed.
- **`CONTROL_DB_KEY` lost**: control DB can't open — readiness fails,
  boot still succeeds (fail-closed at request time, not boot). Restore
  the wrapped blob from the key ceremony store; if the *unwrapping*
  transit key is gone, that's vault-loss — openbao-runbook recovery.
- **OIDC key loss**: boot fails (`OidcKeyMaterial` throws) — protected
  traffic is never served on fabricated keys. Restore the mounted
  secret or re-provision + accept token invalidation.
- **Scanner down** (`VAULT_ADAPTER=minio`): uploads quarantine safely
  (existing behavior — `Unavailable`, never treated as clean);
  `/readyz` reports `scanner`. Recover ClamAV, readiness clears.
- **Cert/key ordering**: bring up vault → cert/secret mounts → API.
  The API tolerates dependency loss *after* boot via readiness but
  requires config completeness *at* boot.

## Forwarded headers

`X-Forwarded-*` is ignored entirely unless `TRUSTED_PROXIES` names the
exact ingress IPs/CIDRs. An untrusted `X-Forwarded-Proto` cannot make
HTTP look like HTTPS; an untrusted `X-Forwarded-For` cannot re-key the
rate-limit partition (proven in `ProductionHostTests`). Reject any
deployment that asks for wildcard trust — the validator already does.
