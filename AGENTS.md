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

## Architecture Decisions (locked — anti-drift)

Locked decisions live in [`docs/adr/`](docs/adr/README.md) (ADR-0001…0010:
language, Dapper/DbUp, SQLite-per-tenant, OpenBao, OpenIddict,
document-store hybrid, adapter seams, masked-by-default, observability,
workflow/coverage). **Milestone and phase plans must conform to them —
see the `milestone-planning` skill.** Any proposed change requires an
amended ADR + explicit user approval before code; never drift silently.
ADR structure is enforced in CI — `node tools/adrcheck.mjs` must stay green.

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
  when its phase starts. **Author sub-issues per the `ticket-contract`
  skill** — Scope / Implementation steps / Negative cases / Acceptance /
  conformance line.
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
- **Wolverine messages pair with their handlers** — never a single
  `Handlers.cs`/`Messages.cs` grab-bag. Each message lives in its own
  file with its handler and any response type unique to it, grouped in
  a feature folder (`src/Lola.Application/<Feature>/<Slice>.cs`, e.g.
  `Admin/Groups/CreateGroup.cs` = `CreateGroupCommand` +
  `CreateGroupHandler`). Shared DTOs and guards get their own files in
  the same folder (`GroupDto.cs`, `AdminGuards.cs` — guards `internal`).
  Handler classes MUST end in exactly `Handler` or `Consumer` —
  conventional discovery matches the suffix literally (`AdminHandlers`
  is not discovered), and any failure is silent at invoke time, not
  startup. Namespaces mirror the folders.
- Web: App Router + React Query; typed fetch helpers in `lib/`; the API
  base URL comes from env (`NEXT_PUBLIC_API_URL`).
- Boy-scout rule: fix trivial adjacent issues in a touched file when
  low-risk; never let it balloon the diff.

## Playbooks (skills — invoked on demand)

Mandates stay here; step-by-step recipes live in `.devin/skills/` so this
file stays inside the always-on context budget. The skill is the
executable form — follow it when the situation matches its description.

- **Runtime evidence** — attach Seq/Jaeger/docker-log evidence to proofs:
  `observability-evidence` skill (SonarQube rescan recipe included).
  **No PII in evidence — ever** (invariant 3).
- **Milestone hardening gate** — OWASP + hardening + ADR-drift pass:
  `milestone-hardening` skill. **A milestone is not Done until its
  hardening-gate comment exists.**
- **Plan close-out** — branch cleanup, docs/wiki/screenshot sweep, SonarQube
  rescan gate: `plan-closeout` skill. Required after the final phase of a
  plan merges, before starting the next.
- **Code size & shape report** — LOC/tests/coverage/rework numbers:
  `code-shape-report` skill. **Required on every ticket marked Done** —
  post it via `save_comment` with or right after the proof comment.
- **Ticket authoring** — sub-issue contract template: `ticket-contract`
  skill (see Linear Workflow).
- **Screenshot capture** — real wiki/proof screenshots:
  `wiki-screenshots` skill.

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
- Local run commands live in the `code-shape-report` skill.

## Verification Commands

```bash
dotnet format --verify-no-changes && dotnet build && dotnet test
node tools/adrcheck.mjs             # ADR structure gate
docker compose up --build        # openbao + seq + jaeger + minio + clamav (+ api/web when scaffolded)
```
