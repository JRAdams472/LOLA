# ADR 0008: masked: by: default

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

PII display — masked-by-default

## Consequences

Masking/suppression is the default state everywhere: API responses mask by field class unless the caller's role→visibility map grants plaintext; UI renders masked; reveal is an explicit authorized action that emits an audit event. Borrower-facing payloads never carry officer-only fields.
# ADR Implements the spec's data-masking layer requirement. Audit events record field classes, never values.

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision



## Consequences


