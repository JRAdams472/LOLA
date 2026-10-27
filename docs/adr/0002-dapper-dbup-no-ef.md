# ADR 0002: dapper: dbup: no: ef

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

Data access — Dapper + DbUp, no EF Core

## Consequences

Data access is Dapper (micro-ORM) + Microsoft.Data.Sqlite + SQLitePCLRaw.bundle_e_sqlcipher. Migrations are embedded append-only migrations/NNNN_name.sql applied at boot via DbUp — same convention as MPEx sqlx and ISPPr sqlc. EF Core is not used anywhere; OpenIddict stores are custom Dapper implementations.
# ADR Explicit SQL keeps the encryption boundary auditable (exactly which ciphertext lands where) and matches sibling hand-SQL convention. Cost: custom OpenIddict stores (~5 interfaces).

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision



## Consequences


