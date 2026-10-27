# M2 Plan — OpenBao & Encryption Foundation (MPE-66)

Two phases → two subtickets → one PR each → milestone hardening gate.

## Contract (from MPE-66)

- OpenBao in compose — file backend + documented unseal for dev;
  sealed-state runbook for real use
- Transit mount `lola-transit`; KEK → per-tenant DEK
  (`transit/keys/tenant-<id>`)
- `IFieldCipher` + Dapper type handlers + `[PiiClass]` driving encryption
  at the repository boundary — repositories never hold plaintext PII
  outside the cipher
- Blind indexes (HMAC) for searchable PII — ciphertext stays randomized
- Rotation via rewrap; crypto-shredding primitive (delete tenant key →
  unrecoverable)
- Secrets in env/docker-secrets only — nothing in the repo

**Conforming ADRs:** 0004 (core), 0002 (Dapper handlers own the cipher
boundary), 0003 (per-tenant keys feed M4 files), 0009 (no PII in
logs/traces).

**Locked design choices (approved during planning):**

- Thin typed `HttpClient` transit client — no VaultSharp. The transit
  surface is ~8 endpoints; a small auditable wrapper beats a
  semi-maintained general SDK for a security boundary.
- Direct transit encrypt/decrypt with `batch_input` — no in-process DEK
  cache; tenant key material never enters API memory.
- CI: detached `docker run` OpenBao dev container in the dotnet job (same
  pattern as the SonarQube job); local tests hit compose. Tests
  hard-require `OPENBAO_ADDR`/`OPENBAO_TOKEN` — no silent skips.
- Dev unseal is automated: an `openbao-init` one-shot service performs
  init → unseal → mount → policy → scoped token, persisting material to
  gitignored `data/openbao/`. Sealed-state ops documented for real use.

## P1 — OpenBao platform

File-backend server replaces dev mode; `compose up` stays zero-friction.

- `openbao/config/openbao.hcl` — file storage `/openbao/file`, TCP :8200
  (tls_disable in dev only — plaintext on the docker net is a documented
  dev carve-out, same as siblings; TLS hops come with deployment work)
- `openbao/init.sh` + `openbao-init` compose service (idempotent):
  init (1-of-1 dev shares) → unseal → `secrets enable -path=lola-transit
  transit` → `lola-api` policy (CRUD on `lola-transit/*`) → scoped token;
  writes `data/openbao/{unseal-key,root-token,api-token}` (gitignored)
- compose: `openbao-init` before api; api mounts `data/openbao` ro,
  `OPENBAO_ADDR` + `OPENBAO_TOKEN_FILE`
- `.env.example`, `docs/openbao-runbook.md` (unseal, sealed-state ops,
  rotation, shred, idx-key reindex note)

## P2 — crypto layer

**Ports** (`Lola.Application/Security/`): `IFieldCipher` (+batch),
`IBlindIndex` → `idx:v<n>:<hmac>`, `ITenantKeyManager` (Ensure/Rotate/
Rewrap/Destroy), `CipherContext` (AsyncLocal tenant id — M4 middleware
populates it per request; tests set explicitly).

**Impl** (`Lola.Infrastructure/`): `OpenBaoTransitClient`,
`TransitFieldCipher`, `TransitBlindIndex` (per-class normalization:
SSN→digits, email→lower/trim, phone→digits; dedicated `hmac`-type key
`tenant-<id>-idx`; versioned `idx:v<n>:` format — idx-key rotation needs
documented reindex), `TransitKeyManager` (rotate/rewrap-batch/destroy
both keys), `PiiAwareTypeMap` (`SqlMapper.ITypeMap`: `[PiiClass]` string
members → ciphertext on write / plaintext on read; `Searchable` members
emit `<name>_idx`). `AddLolaCrypto()` DI; `/api/health` gains `bao` probe.
`PiiClassAttribute` gains `Searchable`.

**New dep:** `Microsoft.Extensions.Http` (MIT). Dapper already present.

**Tests** (`tests/Lola.Infrastructure.Tests/` — env-gated, hard-fail,
never skip): per-tenant round-trip + cross-tenant isolation; batch;
blind-index determinism + normalization; rotate → decrypt works, rewrap
→ `v2:`; shred → decrypt throws; end-to-end type-map test in temp
SQLite — stored SSN is `vault:v1:` ciphertext, `ssn_idx` lookup returns
the row (literal acceptance proof).

## Acceptance → proof

| Bullet | Proof |
|---|---|
| Round-trip per tenant; rewrap; destroyed key ⇒ unrecoverable | Integration test output: rotate + `v2:` rewrap, decrypt-after-delete throws |
| Blind index: SSN lookup returns row while stored SSN is ciphertext | Type-map test: raw row dump shows `vault:v1:` ct + matching `ssn_idx` |
| No secrets in repo | gitleaks green; `data/openbao/` gitignored |
