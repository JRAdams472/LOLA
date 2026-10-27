# Project Rules for Devin — LOLA

LOLA is a **multi-tenant loan origination system**: MISMO-aligned borrower
intake + loan-officer console, storing protected personal data in
SQLCipher-encrypted SQLite files (one per tenant) with OpenBao envelope
encryption, OpenIddict identity, and secured egress to MPEx (PII-free
pricing/eligibility) and ISPPr (document packages).

Backend: C# / .NET 10, ASP.NET Core, **Dapper** + `Microsoft.Data.Sqlite`,
**DbUp** append-only migrations (`migrations/NNNN_name.sql`). No EF Core.
Web: Next.js (App Router) + TypeScript. Dark UI theme: GitHub-dark palette
(near-black surfaces, laser-blue accent) per sibling convention.

## Security Invariants (non-negotiable)

This is **protected personal data**. These rules outrank convenience:

1. **No plaintext PII at rest** — PII-class fields are ciphertext in the DB
   file (OpenBao transit, per-tenant DEK); the file itself is
   SQLCipher-encrypted with a per-tenant key.
2. **PII-free egress contract** — DTOs leaving LOLA to MPEx are statically
   verified to carry no PII-class members (reflection-based CI guard —
   the "PII firewall"). ISPPr payloads carry only template-required fields.
3. **Zero PII in logs/traces** — OTel/log redaction enforced; audit events
   record field *classes*, never values; CI test proves it.
4. **Append-only, hash-chained audit** — every PII read/write attributed
   (actor, tenant, entity, field class, reason). Audit rows are never
   updated or deleted.
5. **TLS on every hop** — API, MinIO, MPEx, ISPPr; service-to-service auth
   via OIDC client-credentials.
6. **Immutable application snapshots** — submitted applications are
   append-only versions. Never UPDATE a submitted snapshot.
7. **Secrets never in repo** — env/docker-secrets; OpenBao for app-level
   secrets. `.devin/mcp_config.local.json` and `data/` stay untracked.
   gitleaks runs in CI.
8. **Masked-by-default UI** — SSN-class fields render masked; reveal is an
   explicit authorized action that writes an audit event.
9. **Crypto-shredding erasure** — erasure destroys tenant data keys before
   files; Reg B/ECOA 25-month retention honored first.
10. **SQLite hygiene pragmas on every tenant connection** —
    `secure_delete=ON`, `auto_vacuum=FULL`, WAL checkpoint discipline,
    `foreign_keys=ON`.

## Ticket Execution — Acceptance Criteria (required)

- **Read the ticket first.** Before writing code for an MPE ticket, read
  the FULL Linear issue — the Scope and Acceptance bullets are the
  contract, not a suggestion.
- **Implement the Scope; prove the Acceptance.** Every Acceptance bullet
  gets a real check — run the command, hit the endpoint, open the page.
- **Stay in scope.** A ticket is done only when ITS acceptance is green.
- **Proof maps 1:1 to Acceptance bullets** — the completion comment on the
  sub-issue addresses each bullet explicitly with run output.
- **Plan-comment for ambiguous tickets** — mechanical tickets go straight
  to implementation; tickets with real design decisions get a short
  implementation-plan comment on the sub-issue when marking In Progress.

## Git Workflow

- Each phase of a plan is developed on its own ticket-prefixed branch:
  `MPE-<n>-phase-<m>` (e.g. `MPE-71-phase-1`). Descriptive slugs allowed
  after the ticket: `MPE-70-openbao`.
- **One PR per phase subticket (required)** — never batch phase tickets.
- **Never commit directly to `main`.** Every change — including bootstrap
  and docs — lands on a ticket-prefixed branch via pull request. Branches
  and PR titles must start with the Linear ticket number:
  `MPE-71: Phase 1 — tenant vaults`.
- **Do not open a PR without a Linear ticket.**
- **Always pause after opening a PR.** Present the PR link + proof, then
  wait for the user's explicit approval — never self-merge, never merge
  on a schedule, never assume approval carries over between PRs.
- Before opening a PR: `dotnet format --verify-no-changes`, `dotnet build`,
  `dotnet test`, and web `lint`/`typecheck`/`test`/`build` all green.
  Commit lockfiles (`packages.lock.json`, `package-lock.json`) — CI fails
  when they drift.
- **No PR or phase merges without the user's explicit approval.** Present
  the PR link + proof, then wait.

## Linear Workflow (required)

- Team **MPE**, project **LOLA**. Every plan gets a **parent ticket**; once
  planning is done and work begins, the parent moves to **In Progress**.
- **Attach the plan document to the parent ticket before creating phase
  sub-issues** — the plan goes on the parent first, then the subtickets.
- Every phase gets a **sub-issue** under its milestone parent named for
  its branch, with scope + acceptance criteria; mark it **In Progress**
  when its phase starts.
- On phase completion, post **proof of completion** on the sub-issue
  **before asking for merge approval and before moving the ticket toward
  Done** — a sub-issue never closes on "looks good":
  - Relevant run logs (build/test/verification output).
  - **Playwright screenshots** of any user-facing change (the `playwright`
    MCP is configured — capture real screens, don't describe them).
  - A link/summary of the **GitHub CI run** showing all checks green.
  - Proof maps 1:1 to the ticket's Acceptance bullets — every bullet gets
    an explicit check, not verification-by-inspection.
- Linear attachments: `prepare_attachment_upload` → PUT the file to the
  signed URL with the exact signed headers →
  `create_attachment_from_upload`. `SignatureDoesNotMatch` means the
  headers drifted — request a fresh URL and PUT immediately (~60s expiry).

## Code Conventions

- C#: minimal APIs or controllers returning typed results; errors map
  through a single error model (RFC 9457 problem details). Dapper type
  handlers own encryption boundaries — repositories never see plaintext
  PII outside the cipher boundary.
- Tenant resolution happens in middleware once per request; every
  data-plane query is scoped to the resolved tenant file — there is no
  code path that can name another tenant's file.
- SQLite pragmas applied on every data-plane connection open.
- Web: App Router + React Query; typed fetch helpers in `lib/`; the API
  base URL comes from env (`NEXT_PUBLIC_API_URL`).
- Boy-scout rule: fix trivial adjacent issues in a touched file when
  low-risk; never let it balloon the diff.

## Evidence Gathering (Observability)

Seq and Jaeger run in the compose stack and are both queryable over plain
HTTP — no MCP needed. Use them to attach runtime evidence (logs, traces)
to phase proof-of-completion.

- **Seq** (structured logs, `http://localhost:5441` — host port remapped
  for sibling coexistence; container still `:80`):
  ```bash
  # Recent events as JSON
  curl -s 'http://localhost:5441/api/events?count=50'
  # Filtered — e.g. errors only (Seq filter expression syntax)
  curl -s 'http://localhost:5441/api/events?count=50&filter=level%20%3D%20%27ERROR%27'
  ```
- **Jaeger** (traces, `http://localhost:17686` — the v2 API is under
  `/api/v3/`, NOT `/api/`):
  ```bash
  curl -s 'http://localhost:17686/api/v3/services'
  # start_time_min/max are REQUIRED and must be RFC3339
  curl -sG 'http://localhost:17686/api/v3/traces' \
    --data-urlencode 'query.service_name=lola' \
    --data-urlencode 'query.limit=10' \
    --data-urlencode 'query.start_time_min=2026-01-01T00:00:00Z' \
    --data-urlencode 'query.start_time_max=2030-01-01T00:00:00Z'
  ```
- **Raw fallback**: `docker logs lola-api-1 --since 30m` (JSON output).
- **SonarQube Community Build** (`http://localhost:9000`, project key
  `lola`): the `sonarqube` MCP server (`.devin/mcp_config.local.json`)
  exposes issues/hotspots/quality-gate/measures tools; the API is plain
  HTTP with token auth (`-u "$TOKEN:"`, token as username, empty
  password). Rescan by mounting **source subdirs only** — a whole-repo
  mount crawls `node_modules` and CI leftovers:
  ```bash
  docker run --rm \
    -v "$PWD/src:/usr/src/src" -v "$PWD/tests:/usr/src/tests" \
    -v "$PWD/migrations:/usr/src/migrations" \
    -e SONAR_TOKEN="$TOKEN" sonarsource/sonar-scanner-cli \
    -Dsonar.host.url=http://host.docker.internal:9000 -Dsonar.projectKey=lola \
    -Dsonar.sources=. -Dsonar.exclusions='**/bin/**,**/obj/**' \
    -Dsonar.cs.vscoveragexml.reportsPaths=coverage.xml
  ```
- **MCP helpers configured** (`.devin/mcp_config.local.json`, gitignored):
  `playwright` (real screenshots for UI proof), `linear` (ticket
  workflow), `github`, `sonarqube`.
- **Boy-scout rule for findings:** when a change touches a file with open
  low/info-severity SonarQube findings, fix them in the same change when
  trivial and low-risk. Skip nontrivial findings rather than ballooning
  the diff.
- **No PII in evidence:** Seq/Jaeger payloads and `docker logs` output
  must never contain plaintext PII — if they do, that's invariant-3
  breakage, file it as a bug, and redact before quoting in tickets.

## Plan Close-Out

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
   isolated `lolashots` stack per `.devin/skills/wiki-screenshots`,
   inspect every PNG for spinners, publish under `images/`, and embed on
   the relevant pages — **and verify no real or realistic PII appears in
   any screenshot** (synthetic borrowers only).
9. **SonarQube rescan gate (required):** if the plan remediated SonarQube
   findings, run the documented rescan after the final phase merges and
   confirm **zero new open issues** beyond the plan's accepted `wontfix`
   list — a finding introduced by the plan's own code is a regression,
   not backlog. Record the final open-issue count in the close-out proof.

## Code Size & Coverage Report (required on parent-ticket close-out)

When a **top-level** Linear ticket closes (all phase sub-issues merged
and verified), post a size-and-shape report as a comment on the parent
ticket via `save_comment`. Report absolute numbers, and deltas vs. the
previous report if one exists on an earlier ticket.

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
5. **Post** — `save_comment` on the parent ticket with the report as a
   markdown table: LOC / executable / test LOC / test count / coverage
   per feature area, plus a one-line caveats section (generated code
   excluded, stale reports, skipped suites).

## Coverage Policy (enforced in CI)

- **Floor: 80% per module/endpoint** — `tools/covercheck.mjs` groups
  coverage (`src/<Project>` granularity for .NET, directory for
  `clients/*`); any module or endpoint area below 80% fails the build.
  Uniformity matters: no ten endpoints at 100% and one at 0%.
- **Goal: 85% overall** — reported by covercheck, directional only.
- **Tests never need tests** — test files and test projects are fully
  exempt from the floor; coverage is measured on source only.
- **Exempt:** generated files (`<auto-generated`, `DO NOT EDIT`, `*.g.cs`,
  `*.designer.cs`), test files/projects (`tests/**`, `*.test.*`,
  `*.spec.*`, `__tests__/`), `migrations/**`, and `Program.cs`
  composition roots (wiring verified via compose/e2e, not unit tests).
- Run locally:
  `dotnet test --collect:"XPlat Code Coverage" -- DataCollectionRunSettings.DataCollectors.DataCollector.Configuration.Format=lcov`
  → `node tools/covercheck.mjs <lcov.info>`; for web,
  `npx vitest run --coverage` → `node tools/covercheck.mjs clients/*/coverage/lcov.info`.

## Verification Commands

```bash
dotnet format --verify-no-changes && dotnet build && dotnet test
docker compose up --build        # openbao + seq + jaeger + minio + clamav (+ api/web when scaffolded)
```
