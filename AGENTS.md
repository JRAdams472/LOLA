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
- **Do not push commits directly to `main`.** Branches and PR titles must
  start with the Linear ticket number: `MPE-71: Phase 1 — tenant vaults`.
- **Do not open a PR without a Linear ticket.**
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
  before asking for merge approval: run logs, **Playwright screenshots**
  for any user-facing change, green **GitHub CI run** link.
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

## Evidence Gathering

- Raw logs: `docker logs lola-api-1 --since 30m` (JSON output).
- Observability stack (Seq/Jaeger/SonarQube) per compose; file a ticket
  when something needs to change rather than silently shipping without it.

## Plan Close-Out

After the final phase of a plan merges:

1. Delete merged phase branches locally and on GitHub (verify with `gh`).
2. No new feature at zero coverage — every new service, handler, or page
   gets at least one unit or integration test.
3. File Linear tickets (project **LOLA**, or the correct sibling project)
   for improvements discovered during the work.
4. Update `README.md` + `docs/` for what shipped — sweep for stale claims.
5. **Size & coverage report** on the parent ticket at close-out.

## Verification Commands

```bash
dotnet format --verify-no-changes && dotnet build && dotnet test
docker compose up --build        # openbao + seq + jaeger + minio + clamav (+ api/web when scaffolded)
```
