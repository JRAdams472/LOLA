# ADR 0004: openbao: envelope: encryption

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

Key management — OpenBao transit

## Consequences

Envelope encryption via OpenBao transit engine (MPL-2.0). KEK → per-tenant DEK hierarchy (transit/keys/tenant-<id>). PII-class fields carry [PiiClass] attributes and are ciphertext via IFieldCipher + Dapper type handlers. Searchable PII (SSN/phone/email) uses deterministic blind indexes (HMAC) — ciphertext stays randomized. Rotation = rewrap; erasure = key destruction (crypto-shredding).
# ADR Two independent layers: SQLCipher file key + field-level transit. OpenBao adds an ops dependency (dev-mode auto-unseal in compose; sealed-state runbook for real deployments). ASP.NET Data Protection and pgcrypto/TDE rejected as coarser-grained.

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision



## Consequences


