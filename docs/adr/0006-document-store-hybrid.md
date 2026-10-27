# ADR 0006: document: store: hybrid

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

Loan data — hybrid relational + JSON1 documents

## Consequences

Canonical MISMO application versions are stored as documents (JSON payload columns, SQLite JSON1) — satisfying 'documents in a data store' — alongside relational index columns for indexed/regulated/queryable fields (status, dates, party identifiers, HMDA demographics, loan terms).
# ADR Document-oriented NoSQL (MongoDB et al.) rejected: schema is a security control (PII classification cannot silently drift), field-level encryption is surgical at column level, integrity constraints matter for financial data, retention/HMDA/audit are query-driven, MongoDB Community is SSPL and its queryable FLE is paid. This is the MPEx artifact pattern.

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision



## Consequences


