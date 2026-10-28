# M5 Implementation Plan — MISMO domain model, intake API & document store

Parent: **MPE-69** — conforms to ADR-0002 (Dapper/DbUp), 0003 (per-tenant
file), 0004 (OpenBao), 0006 (JSON1 doc + index columns), 0007 (seams),
0008 (masked-by-default), 0011 (REST+GraphQL), invariants 1/3/6/8.

User decisions locked at planning: **full 1003 field breadth** via a
field-catalog approach; **hand-rolled subset mapper** for MISMO XML/ULDD
(no maintained OSS .NET translator exists); **officers + borrowers-own**
draft scoping.

## Core design: the field catalog

`src/Lola.Domain/Applications/mismo-field-map.json` (embedded resource)
is the single truth: each entry maps a canonical `path`
(`party[0].ssn`) → `mismoPath` (XML element path) → `ulddField` →
`piiClass` → `index` (`none|ciphertext|blind`) → `validate` rules →
`required`/`core` tiering → `trid` flag. The JSON1 payload, index
columns, validator, and both mappers all derive from it — full breadth
lives in data, not hand-written properties.

## Phases

- **P1 — domain + schema + repositories:** `FieldCatalog`, `ApplicationDocument`,
  `0002_applications.sql` (`applications`, append-only `application_versions`
  JSON1, `application_parties` ciphertext+blind-index cols, blind-index table),
  real repos reshaping the provisional `TenantRepository` seam.
  Acceptance: ciphertext at rest verified; blind-index search by SSN/phone/email.
- **P2 — intake API + validation + lifecycle:** draft→submit→in-review→decisioned
  handler pairs; catalog-driven `FieldError[]` + operator correction-as-new-version;
  immutable submitted snapshots; officers-any / borrower-own scoping; masked reads;
  REST + GraphQL.
- **P3 — MISMO XML + ULDD:** catalog-driven exporter/importer (unknown-element
  tolerant) + ULDD structural export; round-trip proof.
- **P4 — TRID + workflow seam + marquee + gate:** six-field `trid_clock_start` +
  countdown; named-step `POS→Validate→PPE→Fee→Doc→Package` pipeline (Intake/Validate
  real, rest seam-keyed); lifecycle marquee; milestone hardening gate.
