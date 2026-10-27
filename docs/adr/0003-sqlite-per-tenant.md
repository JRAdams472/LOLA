# ADR 0003: sqlite: per: tenant

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

Data plane — SQLite file-per-tenant

## Consequences

Each tenant gets one SQLCipher-encrypted SQLite file (strongest isolation: no cross-tenant query possible, no DB network surface, per-tenant crypto-shredding by key destruction). Control plane is a separate SQLite file (tenant registry, identities, OIDC clients — minimal personal data). Required pragmas on every connection: secure_delete, auto_vacuum=FULL, WAL, foreign_keys.
# ADR Accepted trade-offs: no in-engine authZ/audit/RLS (covered by file isolation + app-layer hash-chained audit); single-writer concurrency (acceptable at scale); deleted-data hygiene enforced via pragmas + VACUUM discipline. Postgres/multi-schema rejected.

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision



## Consequences


