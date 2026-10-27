# LOLA Identity Runbook (M3)

Operational notes for the control-plane identity surface: OIDC clients,
token endpoints, signing material, tenant provisioning, and borrower
invites. Everything here is dev-environment; production deltas are
flagged where they apply.

## OIDC endpoints and clients

Endpoints (all behind `auth-tight` per-IP rate limits where noted):

| Endpoint | Purpose |
|---|---|
| `GET /connect/authorize` | authorization-code + PKCE start (login cookie required) |
| `POST /connect/token` | code exchange, refresh, client-credentials — **10/min/IP** |
| `GET /connect/userinfo` | `sub`, `name`, `tid`, `roles` |
| `GET /connect/logout` | end session |
| `POST /connect/revoke` | token revocation |
| `GET|POST /account/login` | dev login form (Argon2 verify → `lola.auth` cookie) |

Seeded clients (`OidcClientSeeder`, idempotent at boot):

- `lola-officer-console` — public, `http://localhost:3200/callback`, PKCE required
- `lola-borrower-portal` — public, `http://localhost:3300/callback`, PKCE required
- `lola-svc` — confidential, client-credentials, secret from `OIDC_SEED_SVC_SECRET`
- `svc-{tenant}` — confidential, created per tenant by the provisioning
  pipeline; its tokens carry `tid={tenant}` and scope to that tenant only

Registering a new client: insert via `IOpenIddictApplicationManager`
(the seeder and `TenantServiceClientProvisioner` show the pattern) —
secrets are hashed by the manager, never stored plaintext.

Refresh tokens rotate; a redeemed refresh is refused **immediately**
(`RefreshTokenReuseLeeway = 0`) — strict reuse detection is an M3
acceptance criterion, so callers must retry on concurrency, not replay.

## Dev signing and encryption keys

`OIDC_SIGNING_KEY_PATH` / `OIDC_ENCRYPTION_KEY_PATH` (default
`data/oidc/`, gitignored). `DevSigningKey.LoadOrCreate` generates an
RSA-2048 key on first run, writes it atomically, and regenerates on a
corrupt file. Deleting a key file rotates it on next boot — all
outstanding tokens issued under the old key become invalid (dev: fine;
prod: needs a proper key source — see below).

**Production note:** file keys are dev-only. A real deployment should
source signing/encryption material from a KMS/HSM or mounted secret —
the seam is `options.AddSigningKey`/`AddEncryptionKey` in `Program.cs`.

## Tenant provisioning

`POST /api/admin/tenants` (TenantAdmin or Service policy) runs the
re-entrant pipeline:

`registry → transit-keys → data-file → activate → svc-client`

- Steps are idempotent; a row stuck in `provisioning` IS the failure
  state — re-POST to resume.
- `serviceClientSecret` is returned **once**, on first creation;
  re-runs return `null`. Record it immediately — only its hash is kept.
- `GET /api/admin/tenants/{id}` — status view (own-tenant for user
  tokens, any for Service).

Env: `TENANT_DATA_DIR` (default `data/tenants`) — the directory holding
SQLCipher files, one per tenant.

## Borrower invites

`POST /api/admin/tenants/{id}/invites` (TenantAdmin/LoanOfficer):

- 256-bit token; only its SHA-256 hash is stored (a DB read can never
  yield a usable token), 72h TTL, single-use enforced atomically.
- `PORTAL_BASE_URL` (default `http://localhost:3300`) builds the link —
  in dev the link is returned in the response for the officer to hand
  over; a real impl swaps `IInviteDelivery` to send email.
- `POST /api/invites/accept` (public, **10/min/IP**) — set-password;
  creates an active Borrower in the tenant's `borrowers` group.

Ops: an unused/expired invite is inert — re-invite creates a fresh
token (the unique constraint is on `token_hash`, not email).

## Rates and limits

- `auth-tight` partition: 10/min per IP **per endpoint** on
  `/connect/token`, `/api/invites/accept`, and `/account/login`
  (brute-force surface).
- Global limiter: 200/min fixed window.

## Deployment requirements

- **TLS at the edge is mandatory (invariant 5).** The OpenIddict server
  disables its own transport-security check because Kestrel sees plain
  HTTP in dev/tests and behind a TLS-terminating proxy — tokens would
  flow plaintext if the API were ever exposed over bare HTTP. Never
  bind the API directly to an untrusted network without TLS in front.
- **`lola.auth` session cookie** is `Secure` outside Development; the
  same edge-TLS assumption applies.
- **OpenAPI document** is served in Development only.
- **Key material:** file-based dev keys under `data/oidc/` are not for
  production — source signing/encryption keys from a KMS/HSM or mounted
  secret via `options.AddSigningKey`/`AddEncryptionKey` (see above).

## Related docs

- `docs/openbao-runbook.md` — transit keys, unseal, crypto-shred
- ADR-0005 (identity), ADR-0008 (masked-by-default), ADR-0011 (seams)
