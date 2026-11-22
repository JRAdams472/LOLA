---
name: code-shape-report
description: Generate the LOLA code size & shape report (LOC, test split/counts, coverage, rework metric) that must be posted as a Linear comment on every ticket marked Done. Use when marking a ticket Done, posting proof-of-completion, or when asked for LOC/coverage stats.
---

# Code size & shape report (required on every ticket marked complete)

Post a code-shape report as a comment on **every** Linear ticket when it
is marked Done — phase sub-issues and parent tickets alike. It's fun to
watch the project change; the per-phase granularity is the point. Post
it via `save_comment` together with (or right after) the proof comment.
Report absolute numbers, and deltas vs. the previous report if one
exists on an earlier ticket.

Generate it as follows:

1. **Per-language LOC** — tokei (installed via winget; if not on PATH it's
   at `%LOCALAPPDATA%\Microsoft\WinGet\Packages\XAMPPRocky.Tokei_*\tokei.exe`):
   ```bash
   tokei -e clients/*/node_modules -e '**/bin' -e '**/obj' src tests clients migrations
   ```
   Call out generated code separately in the report.
2. **Source vs. test split** — bucket tracked files (`git ls-files`) by
   path: C# tests are `tests/**` + `*.Tests.csproj` files; web tests are
   `clients/*/__tests__`, `e2e`, `mocks`. Everything else is source. Sum
   `wc -l` per bucket; "executable lines" = non-blank non-comment.
3. **Test counts** — `grep -c '\[Fact\]\|\[Theory\]'` per test project;
   count `it(|test(` in web `__tests__` + `e2e`.
4. **Coverage** —
   - .NET: `dotnet test --collect:"XPlat Code Coverage"` (lcov) — report
     per-project numbers; `node tools/covercheck.mjs <lcov.info>` for the
     floor check.
   - Web: `npm test -- --coverage` (or read `coverage/lcov.info` — always
     note its date; stale reports must be flagged).
   - Local runs:
     `dotnet test --collect:"XPlat Code Coverage" -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=lcov`
     → `node tools/covercheck.mjs <lcov.info>`; for web,
     `npx vitest run --coverage` → `node tools/covercheck.mjs clients/*/coverage/lcov.info`.
5. **Rework** — fix-up commits tell you whether rules changes actually
   reduce agent drift; report both numbers:
   - This PR: `git log origin/main..HEAD --format='%s' | grep -icE 'fix|silence|warning|sweep|ci|format|lint'`
   - Rolling: same pattern over `git log --format='%s' -300`, as a %.
   - Report line: `Fix-up commits this PR: N · rolling X% of last 300 commits`.
6. **Post** — `save_comment` on the parent ticket with the report as a
   markdown table: LOC / executable / test LOC / test count / coverage
   per feature area, plus a one-line caveats section (generated code
   excluded, stale reports, skipped suites).
