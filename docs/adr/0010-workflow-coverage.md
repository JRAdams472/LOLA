# ADR 0010: workflow: coverage

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

Workflow & coverage

## Consequences

Never commit to main; every change via ticket-prefixed PR (MPE-<n>-phase-<m>); always pause for explicit user approval after opening a PR — never self-merge. One PR per phase subticket. Proof-of-completion posts on the subticket before merge approval AND before ticket close, mapped 1:1 to acceptance bullets. Coverage: 80% floor per module/endpoint (CI via tools/covercheck.mjs), 85% goal; test files/projects exempt.
# ADR Codified by user direction during phase 0; mirrors sibling conventions.

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision



## Consequences


