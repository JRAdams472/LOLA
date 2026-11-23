# ADR 0004: OpenBao envelope encryption

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

Key management — OpenBao transit

## Consequences

Envelope encryption via OpenBao transit engine (MPL-2.0). KEK → per-tenant DEK hierarchy (transit/keys/tenant-<id>). PII-class fields carry [PiiClass] attributes and are ciphertext via IFieldCipher + Dapper type handlers. Searchable PII (SSN/phone/email) uses deterministic blind indexes (HMAC) — ciphertext stays randomized. Rotation = rewrap; erasure = key destruction (crypto-shredding).
Two independent layers: SQLCipher file key + field-level transit. OpenBao adds an ops dependency (dev-mode auto-unseal in compose; sealed-state runbook for real deployments). ASP.NET Data Protection and pgcrypto/TDE rejected as coarser-grained.

Control-plane extension (MPE-276 / Phase 7, design `docs/control-plane-encryption-design.md`): the control-plane file is SQLCipher-keyed like every tenant file. Its file key is a random 32-byte value wrapped by a dedicated transit key `transit/keys/control-plane` and provisioned to the runtime as a wrapped blob (`CONTROL_DB_KEY`), unwrapped at startup via OpenBao with an independent workload identity — never a row inside the locked database. A missing or invalid bootstrap fails the open (no plaintext fallback); an OpenBao outage makes the control plane not-ready rather than unkeyed. Control-plane identity columns (user/invite emails and names, tenant slug and display name, group names, OIDC `properties`) are field-level ciphertext: tenant-scoped identity fields ride the owning tenant's transit key so erasure crypto-shreds control-plane copies, and control-plane-global rows use `transit/keys/control-plane`. Lookups use keyed normalized blind indexes (HMAC, same key domain) — no global or unsalted index exists. File-key rotation is `PRAGMA rekey` plus transit rewrap of the wrapped blob; field-key rotation is transit rewrap, both offline runbook operations.
