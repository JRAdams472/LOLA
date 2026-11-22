---
name: plan-closeout
description: Close out a LOLA plan after its final phase merges — branch cleanup, zero-coverage check, docs/README/wiki refresh, screenshot sweep, SonarQube rescan gate. Use when the last phase of a milestone plan merges, before starting the next plan.
---

# Plan close-out

After the final phase of a plan merges, before starting the next:

1. Delete merged phase branches locally and on GitHub (verify PR state
   with `gh` first).
2. Ensure no new feature is left at zero test coverage — every new
   service, handler, or page needs at least one unit or integration test.
3. Re-walk corrected audit/threat-model findings for regressions.
4. File Linear tickets (project **LOLA**, or the correct sibling project —
   e.g. ISPPr for e-signature) for improvements or new features inspired
   by the completed work.
5. Update `README.md` so features and architecture reflect what shipped —
   including sweeping for stale claims (endpoint lists, env vars,
   workflow names).
6. Review every doc listed in `README.md`'s Documentation section plus
   touched `clients/*/README.md` files for staleness.
7. Update the GitHub wiki (`LOLA.wiki.git`) — it lives outside the repo,
   so clone it, add/refresh pages for what shipped, and push to `master`.
8. Refresh wiki **screenshots** when user-facing screens changed: run the
   isolated `lolashots` stack per the `wiki-screenshots` skill,
   inspect every PNG for spinners, publish under `images/`, and embed on
   the relevant pages — **and verify no real or realistic PII appears in
   any screenshot** (synthetic borrowers only).
9. **SonarQube rescan gate (required):** if the plan remediated SonarQube
   findings, run the documented rescan (see the `observability-evidence`
   skill) after the final phase merges and confirm **zero new open
   issues** beyond the plan's accepted `wontfix` list — a finding
   introduced by the plan's own code is a regression, not backlog.
   Record the final open-issue count in the close-out proof.
