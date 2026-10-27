---
name: milestone-planning
description: Plan a LOLA milestone or phase without drifting from locked architecture. Use whenever creating or refining a phase plan, subticket scope, or implementation approach for an MPE ticket in this repo — reads the ADRs and megaplan first and flags deviations instead of silently changing direction.
---

# Milestone planning (anti-drift)

LOLA's architecture is locked by the MPE-63 megaplan. Plans that quietly
change locked decisions are defects — this procedure exists to catch them.

## Before writing any plan

1. Read `AGENTS.md` — security invariants + workflow rules are binding.
2. Read `docs/megaplan-mpe-63.md` — milestone ordering + dependencies.
3. Read every `docs/adr/NNNN-*.md` that touches the milestone's scope —
   at minimum the data-access, encryption, identity, and adapter-seam ADRs.
4. Read the **full Linear ticket** — Scope/Acceptance bullets are the
   contract, not the title.

## While planning

- Cite the ADRs each phase conforms to (e.g. "per ADR-0003, one file per
  tenant"). If a phase can't be expressed within an ADR, that's a signal,
  not an obstacle — stop.
- **Never silently substitute** locked choices: no EF Core, no Postgres,
  no alternate IdP, no unencrypted-at-rest shortcut, no PII in an egress
  DTO "just for now," no skipping masking.
- Reused patterns from siblings are fine; reused *decisions* must be
  checked — sibling Postgres/Go/Rust choices do not apply here.
- Vendor/integration work goes through the ADR-0007 seams; direct calls
  to MPEx/ISPPr/fee/vendor endpoints from business logic are drift.

## When a deviation is genuinely needed

1. Name the ADR it conflicts with and why the current decision fails.
2. Propose an **amended ADR** (new file revision, Status → Superseded for
   the old decision, link both ways) in the plan/PR.
3. Get explicit user approval on the amended ADR **before** any code —
   the amendment lands in the same or a preceding PR, never after.

## Checklist for a conforming plan

- [ ] Milestone ticket read in full; scope matches ticket, not plan-file
      memory
- [ ] Every phase names its conforming ADRs
- [ ] Security invariants mapped to at least one acceptance check each
- [ ] Branch/PR naming follows `MPE-<n>-phase-<m>`; one PR per subticket
- [ ] Proof-of-completion plan: what logs/screenshots/CI runs prove each
      acceptance bullet
- [ ] Plan ends with the **milestone hardening gate** (OWASP + app
      hardening + ADR-drift review — findings comment on the ticket
      before close) per `AGENTS.md`
