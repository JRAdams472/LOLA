# LOLA M14 (MPE-78) — Hardening, UAT &amp; close-out

Five-phase plan to close the MPE-63 megaplan: STRIDE threat model + enforced dependency-vuln CI gates, live drills (backup/restore, crypto-shred, retention, OpenBao DR), a per-bullet UAT sweep of all milestone tickets, wiki/screenshot refresh, then close-out tickets, megaplan size-and-shape, and the M14 deep hardening gate.

## Context

M14 = "prove the security posture end-to-end, then close the megaplan cleanly" (MPE-78). Docs/ops/drills milestone, not features. User decisions locked:

- **UAT:** live re-verify per bullet — every acceptance bullet on every milestone ticket gets a fresh check; heavyweight runs (coverage, e2e) re-run once as sweep proof.
- **Vuln gate:** built-in scanners — `dotnet list package --vulnerable --include-transitive` + `npm audit --audit-level=high` as failing CI steps; add `clients/portal` to `dependabot.yml`.
- **5 phases**, one PR per subticket, `MPE-<n>-phase-<m>` branches.
- **Drill proof:** execute per runbook, paste output in Linear comments — no committed drill scripts/reports.

### Existing surface (researched)

- `POST /admin/tenants/{id}/backup` + `/restore` (VACUUM INTO + sha256 manifest) — `src/Lola.Api/Admin/AdminEndpoints.cs:53,65`
- `DELETE /admin/tenants/{id}?force=` → `EraseTenantCommand` — crypto-shredding, Reg B gate + force/reason audit override (`AdminEndpoints.cs:75`, `EraseTenant.cs`)
- `GetTenantRetentionQuery` → 25-month per-application retention report (`src/Lola.Application/Applications/GetTenantRetention.cs`)
- `docs/openbao-runbook.md` — covers dev unseal; **no prod DR section** (auto-unseal, multi-share, token revocation)
- `docs/data-plane-runbook.md` — backup/restore/erasure operator surface documented
- CI: gitleaks ✓ enforced, covercheck floor ✓ enforced, PII-firewall tests ✓ (via `dotnet test`); **dependency scan missing**; `dependabot.yml` covers nuget/npm(web)/actions/docker/compose but **not `clients/portal`**
- No prior `docs/threat-model.md`, no drill tooling
- Docs gap: `docs/m9-plan.md` absent (m1–m8, m10–m13 present) — fix in P4
- Open follow-ups to disposition at close: MPE-215 (dev npm audit — the `next build` fatal nudge for Next 16.4.3 makes this live), MPE-225 (borrower vocab), MPE-231/232 (vendor seam)

### Expected interaction: the new `npm audit` gate will almost certainly flag Next.js 16.4.3 (the security-fatal nudge seen in M13 builds). Plan for a Next.js bump in Phase 1 — this also closes the MPE-215-adjacent risk properly rather than carrying it.

## Conforming ADRs

| Concern | ADR |
|---|---|
| Threat model over tenant-file + envelope-encryption flows | 0003, 0004 |
| Identity/roles in UAT + drills | 0005 |
| Egress flows in threat model (MPEx/ISPPr/vendor/Zara) | 0007 |
| Masking + audit claims under UAT re-verification | 0008, 0009 |
| Coverage gate + CI enforcement review | 0010 |
| Ingress surfaces (webhooks, BFFs) in STRIDE | 0011 |

No deviations anticipated — this milestone verifies conformance rather than adding architecture.

## Phases

### Phase 1 — STRIDE threat model + CI gate enforcement

Branch `MPE-<n>-phase-1`. Deliverables:

- `docs/m14-plan.md` — this plan, committed (convention).
- `docs/threat-model.md` — STRIDE per PII flow stage: **ingress** (officer web BFF, portal BFF, GraphQL/REST, vendor webhooks, admin API), **at-rest** (SQLCipher file, transit envelope, MinIO vault, backups), **egress** (MPEx firewall, ISPPr manifest, vendor allowlist, Zara fees), **logs/traces** (Seq/Jaeger, audit classes-only), **deletion** (crypto-shred, retention clock, backup shredding). Each: trust boundary → threats (S/T/R/I/D/E) → existing mitigation → invariant/ADR anchor → residual risk → follow-up ticket if needed.
- CI additions in `.github/workflows/ci.yml`: `dotnet list package --vulnerable --include-transitive` (fail on vulnerable), `npm audit --audit-level=high` for `clients/web` and `clients/portal`.
- `dependabot.yml`: add `clients/portal` npm entry.
- Fix whatever the new gate flags (expected: Next.js bump both clients; lockfiles committed).
- Verify + document that gitleaks / covercheck / PII-firewall are enforced (comment evidence on ticket).

**Proof:** CI run green with new steps visible; threat-model doc; audit output showing what was flagged/fixed.

### Phase 2 — Drills + OpenBao DR runbook

Branch `MPE-<n>-phase-2`. Deliverables:

- `docs/openbao-runbook.md` + DR section: sealed-vault recovery, production multi-share unseal + auto-unseal options, root-token revocation/rotation, transit-key loss consequences, restore-from-backup sequence.
- `docs/data-plane-runbook.md` — add retention dry-run procedure if missing.
- **Execute on a scratch tenant** (create `drill` tenant — never demo):
  1. Backup drill: POST backup → verify manifest sha256 → mutate → POST restore → data verified intact + manifest check log.
  2. Crypto-shred drill: erase tenant → verify tenant file + backups destroyed + unrecoverable (attempt restore → fails; key gone from OpenBao).
  3. Retention dry-run: retention report → erase attempt on in-window app → blocked → forced erase w/ reason → `retention_override` audit event.
  4. OpenBao unseal drill: seal → confirm api degraded → unseal → recovery.
- Proof pasted as Linear comment output (user's chosen artifact format).

**Proof:** command transcripts per drill; audit events shown for forced erase; unseal sequence log.

### Phase 3 — UAT sweep + observability

Branch `MPE-<n>-phase-3`. Deliverables:

- `docs/uat-matrix.md` — every acceptance bullet from every milestone ticket (MPE-64…MPE-78 chain: M1–M14), each row: bullet → live check executed → result → evidence (endpoint hit, screenshot, run log).
- Live re-verification per bullet on the compose stack; heavyweight proof re-run once: full `dotnet test` + coverage floor, web+portal jest, marquee e2e (M12 + M13).
- Observability sanity: Seq queryable, Jaeger traces flowing, dashboard queries work; **PII-in-logs spot audit** broadened beyond M13's vendor-only sweep — sample all services for SSN/name-shaped values.
- Findings → Linear tickets (noncritical) or in-phase fixes (critical).

**Proof:** UAT matrix doc all-green; suite + coverage run; Seq/Jaeger queries; PII sweep results.

### Phase 4 — Wiki refresh + screenshots

Branch `MPE-<n>-phase-4`. Deliverables:

- Full `LOLA.wiki` pass: verify every page current post-M13, add missing coverage (threat-model summary, runbook index, UAT/close-out page), fix `docs/m9-plan.md` gap in-repo.
- `README.md` final sweep — features, env vars, endpoint lists vs. shipped reality.
- `lolashots` re-run for any stale screens; inspect every PNG (synthetic data only, no spinners).

**Proof:** wiki commits pushed; PNG inspection notes; README diff.

### Phase 5 — Close-out + M14 hardening gate

Branch `MPE-<n>-phase-5`. Deliverables:

- **M14 hardening gate** = deep whole-system pass (per megaplan: "the deep pass across the whole system on top of the per-milestone gates"): OWASP + app-hardening + ADR-drift review, findings comment on MPE-78, criticals fixed in-phase, rest ticketed.
- **Close-out tickets filed:** ISPPr e-signature/ESIGN e-consent → **ISPPr project**; MPEx contract tests → **MPEx project**; real vendor selection → **LOLA**; workflow orchestration UI → **LOLA**. Disposition existing follow-ups (MPE-215/225/231/232) in the comment.
- **Size-and-shape report on MPE-63** — megaplan-level totals (tokei LOC, test counts, coverage, per-milestone file/line deltas).
- Close-out: README/docs verified, MPE-78 → Done; flag MPE-63 milestone close to user.

**Proof:** gate comment; ticket links; size-and-shape comment; milestone closed.

## Risks / watch-items

- **Drill destructiveness:** all drills on a scratch `drill` tenant only; demo tenant untouched. Forced-erase audit event is expected output.
- **npm audit gate will likely block P1** on Next.js 16.4.3 — bump early, commit lockfiles.
- Docker-engine bounces have happened twice this session — re-verify sibling stacks before drills/UAT.
- `m9-plan.md` doc gap — recreate or note as intentionally absent in P4.
