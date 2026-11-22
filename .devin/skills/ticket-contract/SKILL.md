---
name: ticket-contract
description: Author LOLA Linear phase sub-issues in the MPE-266 contract style — Scope, Implementation steps, Negative cases, Acceptance, ADR conformance. Use whenever creating or refining MPE tickets, phase sub-issues, or subticket scope for this repo.
---

# Ticket contract (sub-issue authoring)

Every phase sub-issue is the contract the implementer works against. The
MPE-266 shape is the reference — tickets written this way land cleanly
because the implementer never has to guess scope or invent checks.

## Required sections

1. **Header** — `M<n> Phase <m> of <k>` under the milestone parent, the
   branch name, and the plan doc path (e.g. `docs/m19-plan.md`).
2. **Scope** — what lands, bounded. Name the seams/surfaces being added
   or changed, not just the goal.
3. **Implementation steps** — the leverage section:
   - name the files/handlers to **read first**;
   - name the *transformation*, not just the outcome ("replace
     load-all/decrypt-all/filter-later with parameterized predicates —
     filter before paging and ciphertext loading");
   - call out contracts to preserve ("keep the 403 contract without
     exposing inaccessible existence").
4. **Negative cases** — the deny/rollback/no-op matrix to prove:
   revoked/unlinked/cross-tenant stays out, deny-immediately-after-unlink,
   no staff DTOs leak into partner payloads, etc.
5. **Acceptance** — testable bullets, one check each. Proof at completion
   maps 1:1 to this list — write bullets you can *run*, not verify by
   inspection.
6. **Conformance line** — `Conforms ADR-NNNN; invariants X, Y.` so the
   anti-drift check is explicit.

## Single-contract rule

One contract per ticket. If a ticket must carry a second contract (e.g. a
security-remediation phase piggybacking on a milestone phase, as MPE-266
did), the second contract gets its own `## ` section with **partitioned
acceptance bullets** and named prerequisite tickets — never blended into
the primary scope.

## Anti-patterns

- Scope written as outcomes ("make partner reads fast") with no named
  files or transformations.
- Acceptance bullets that can't be checked by running something.
- Missing negative cases on any security-adjacent change — the deny
  matrix IS the acceptance for scoped-access work.
