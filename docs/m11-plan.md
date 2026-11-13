# M11 (MPE-74) — Officer console plan

Build the LOLA officer console at clients/web (Next.js 16 + MUI + React Query, GitHub-dark, BFF token custody per MPEx sibling pattern): Pipeline with TRID countdown, tabbed 1003 Loan Workspace with audited PII reveal, 3-column Closing Costs & Docs, blind-index loan search, plus admin surfaces — reads through GraphQL per ADR-0011, commands via REST.

## Context & decisions confirmed

Milestone **M11 / MPE-74** — the loan-officer/admin UI. Depends on M5–M10, all merged. User decisions for this plan:

- **Reads via GraphQL** (`/graphql`, HotChocolate) per ADR-0011; commands (price/fees/packages/lock/reveal/upload/search is a query) via REST. Existing `Query` gets expanded, not replaced.
- **Pipeline list enriched server-side** — add `BorrowerName` (visibility-masked) + `TridDeadlineUtc` to the list read model; no N+1, no TS re-implementation of the Reg Z holiday calendar (`TridClock.DeadlineUtc` stays the single source).
- **Docs dashboard carries both** generated ISPPr packages and M10 vault uploads (status chips + audited download).
- **Extras included**: per-application + tenant audit viewers, user/group admin, borrower invites.

Sibling pattern to reuse ("skills acquired through other web projects"): **MPEx `clients/web`** is the template — BFF route attaching the bearer server-side (browser never sees a token), auth-code+PKCE with sealed httpOnly JWE session cookie + refresh-rotation dedup, `proxy.ts` login bounce, MUI GitHub-dark theme, dense-table Operate conventions from its `PRODUCT.md`. The `impeccable` skill governs design quality (run `init` → PRODUCT.md, write DESIGN.md, read `craft-floor.md` before UI edits); `wiki-screenshots` (lolashots) produces the visual proof at close-out.

## API surface consumed (all exists today)

| Console need | Endpoint / handler |
|---|---|
| List apps | `GET /api/tenants/{t}/applications` → `ListApplicationsQuery` → `ApplicationMeta` |
| Blind search | `GET …/applications/search?field&value` → `SearchApplicationsQuery` (fields: `ssn,email,phone,first_name,last_name,street`; staff-only, audited) |
| App detail (masked doc) | `GET …/applications/{id}` → `ApplicationDto.Doc` masked by `DocMasker` |
| Draft edit / submit / correct / transition | `PATCH /{id}`, `POST /{id}/submit|/correct|/transition` (422 carries `FieldError[]`) |
| Create draft | `POST …/applications` |
| Price / lock | `POST /{id}/price` (202 outbox), `GET /pricing[history]`, `POST /{id}/lock|/unlock`, `GET /lock` |
| Fees | `POST /{id}/fees` (202), `GET /fees[history]`, `PATCH /fees/{itemId}` override |
| Packages/docs | `POST /{id}/packages` (202), `GET /documents[/{docId}[/download]]` (302 presigned) |
| Vault | `GET /{id}/uploads[/{docId}[/download]]` (302 capability URL) |
| Audited reveal | `POST /{id}/reveal` `{reason}` → unmasked `ApplicationDto` (`RevealPii` policy: TenantAdmin+LoanOfficer) |
| Audit | `GET /{id}/audit`, `GET /api/tenants/{t}/audit`, `GET …/retention` |
| Admin | `/api/admin/tenants/{t}/users|groups|invites` CRUD + membership (all exist) |
| OIDC | `lola-officer-console` public client already seeded — PKCE required, redirect `http://localhost:3200/callback`, `lola-api` + `offline_access` scopes |

Role→visibility (seeded): LoanOfficer sees Name/Address/Contact/Income/Other plaintext; Ssn/Dob/Account stay masked → audited reveal. TenantAdmin sees all.

## Known backend work (in scope)

1. `ApplicationMeta` += `BorrowerName` (join `application_parties` party_index 0, decrypt via existing type handlers, mask through the same visibility pipeline) + `TridDeadlineUtc` (`TridClock.DeadlineUtc(tridClockStart)`).
2. GraphQL `Query` expansion — thin resolvers over existing handlers: `applications`, `application`, `search`, `pricing`, `pricingHistory`, `fees`, `feeHistory`, `rateLock`, `documents`, `uploads`, `steps`, `applicationAudit`, `tenantAudit`, `tenantGroups`, `retention`. Mutations stay REST. Existing guards (depth 8, exception detail dev-only) unchanged.
3. Restyle the `/account/login` placeholder (comment says it awaits M11) — dark-theme page, `err` display, keep Argon2 verify + `auth-tight` limit + local-redirect-only.

## Phases (one subticket + `MPE-<n>-phase-<m>` branch + one PR each)

### Phase 1 — `clients/web` scaffold, BFF auth, app shell
- `clients/web`: Next.js 16 App Router + React 19 + TS + MUI 9/Emotion + `@tanstack/react-query` + `jose`; eslint flat config, jest + Testing Library, playwright config — dependency set mirrored/pinned from MPEx `clients/web/package.json`.
- `lib/auth.ts` (adapted from MPEx): PKCE+state cookies, `/connect/authorize`→`/connect/token` exchange, sealed `lola_session` JWE cookie (`SESSION_SECRET`, no fallback), refresh dedup + rt-bounded maxAge. Routes: `app/auth/login`, `app/callback` (matches seeded redirect), `app/auth/logout`, `app/auth/me` (exposes roles for UI gating).
- `app/bff/[...path]/route.ts`: same-origin proxy attaching the bearer; per-route body caps — 2 MB default, ~26 MB streamed (`duplex`) only for `…/uploads`. `proxy.ts` middleware bounce to login.
- Shell: `app/theme.ts` (GitHub-dark ported from MPEx), `providers.tsx`, `components/AppShell.tsx` (nav: Pipeline, Loan Search, Admin — role-gated; identity+role chip; sign-out), `StatusChip`, `ErrorPanel`, empty-state-verbs conventions.
- Styled `/account/login` page in the API; compose `web-officer` service on :3200 (commented block exists — CORS_ORIGIN already correct); `Dockerfile`; `.env.example` additions.
- `PRODUCT.md` via impeccable init; `DESIGN.md` for the console's locked visual world.
- Tests: auth unit tests (state/PKCE/session edges), shell spec, playwright `auth.setup` + smoke.
- ADRs 0005/0011; invariants 5, 7.

### Phase 2 — GraphQL read-model + Pipeline + loan search
- Backend items 1–2 above (meta extension + Query expansion + resolver/handler tests proving masking parity REST↔GraphQL per ADR-0011).
- `/` Pipeline view: dense table — Loan ID, Borrower Name, StatusChip (`Data Entry→Priced→Fees Calced→Disclosures Generated` + step event), `TridCountdown` (deadline−now; amber <48h, red overdue, muted "clock not started"), newest-first, row→`/loans/{id}`.
- `/search`: field select + value → GraphQL `search` (POST body — an SSN never rides a URL/history) → results as pipeline-style rows; **New Loan** button → create-draft dialog → workspace.
- Tests: meta projection (name masking per role, deadline math), resolver masking-parity tests, jest countdown, playwright pipeline+search specs.

### Phase 3 — Loan Workspace: tabbed 1003 + correction + reveal
- `/loans/{id}`: header (id, borrower, StatusChip, TRID chip, step); tabs **Borrower(s) / Employment & Income / Assets / Liabilities / Property & Loan / Steps / Audit(staff)** — sections keyed to the field catalog (`party[]`, `employment[]`, `asset[]`, `liability[]`, `subject_property`, `loan`).
- `lib/field-map.ts` generated/derived from `docs/pii-data-map.json` + catalog conventions → label/type/enum/piiClass per path; `MaskedValue` component renders `•••xxxx` with reveal affordance (staff roles only, from `/auth/me`).
- Edit loop: dirty-field merge-patch → `PATCH` (draft) / `POST /correct` (submitted — append-only version, invariant 6) / `POST /submit`; 422 `FieldError[]` mapped onto exact inputs (validation-correction requirement).
- `RevealDialog` (required reason) → `POST /reveal` → unmasked doc + persistent "PII revealed — audited" banner + re-mask control; affordance hidden for borrowers/non-staff.
- Steps tab: step-event timeline. Audit tab: `GET /{id}/audit` table (staff).
- ADRs 0006/0008/0011; invariants 4, 6, 8.

### Phase 4 — Closing Costs & Docs + admin surfaces
- `/loans/{id}/closing` three columns:
  - **Fees**: itemized list by LE section A/B/C/E; Recalculate (`POST /fees` 202 → poll `GET /fees`); override dialog (amount+reason → `PATCH /fees/{itemId}`); history expander.
  - **Pricing/Lock**: active lock card (product/rate/term/expiry) + Lock/Unlock(reason) dialogs + Re-Price (202 → poll); history expander.
  - **Docs**: Generate Package (kind+reasonCodes → 202 → poll registry); documents grid with download (follow 302 → presigned); vault uploads grid (filename, classification, `quarantined/clean/infected` chips, download mint → 302 → capability URL).
- `/admin`: Users & Groups (list, group CRUD + membership), Borrower Invites (create → link shown once), Tenant Audit (actor/action/date filters), Retention report.
- ADR-0007 (UI never touches MPEx/ISPPr/MinIO directly — all egress via LOLA/BFF), 0008, 0011; invariants 3, 4, 8.

### Phase 5 — Marquee e2e + milestone hardening gate + close-out
- Playwright marquee on the real compose stack: login → create → fill → submit → price → lock → fee override → package → download → SSN blind-search → masked→reveal; screenshots attached to subticket proof.
- **Hardening gate (required before MPE-74 closes):** OWASP review of the console+BFF — cookie flags, CSP/security headers (`next.config` headers), open-redirect on `next`/`returnUrl`, upload body caps, GraphQL depth/cost limits, SSN-never-in-URL verification, dependency freshness (≥7-day rule), Seq check that e2e runs logged zero PII; ADR-drift review; findings comment on MPE-74.
- Close-out: code-shape report, README M11 section, wiki Officer-Console page + lolashots screenshots (synthetic borrowers only), branch cleanup, follow-up tickets.

## Acceptance map (MPE-74)

| Ticket acceptance | Proof |
|---|---|
| Three views functional vs live API; TRID countdown on pipeline rows | Playwright specs + screenshots per phase (P2/P3/P4) + marquee (P5) |
| Search by SSN/phone/email/name/address returns correct loan | `searchApplications` resolver test per field + e2e SSN search spec |
| PII masked by default; reveal authorized + audited | masked-render unit tests, RevealPii 403 for borrower, e2e reveal spec + `audit_events` row shown |

## Risks / honest flags

- **GraphQL breadth** is the largest new surface; resolvers must stay dumb passthroughs — masking parity is proven by test, not assumed.
- **`BorrowerName` on meta** decrypts party ciphertext per row — fine at MVP scale; flagged if pipelines grow.
- **Reveal downloads unmasked doc to the browser** — transient in-memory only, banner + re-mask; nothing cached in React Query beyond the reveal view.
- **BFF upload streaming** (≤25 MB vault docs) needs `duplex` passthrough — verify in e2e, not just unit tests.
- Dev OIDC client redirect is `http://localhost:3200/callback` — console uses `/callback` verbatim so existing seeded dev DBs keep working.

## Workflow notes (per AGENTS.md)

Plan doc lands in `docs/m11-plan.md` and is attached to **MPE-74** before phase subtickets are cut; MPE-74 → In Progress at Phase 1 start. One PR per subticket, green gates (`dotnet format/build/test`, web lint/typecheck/test/build), proof comment + pause for merge approval after every PR. Milestone ends only with the hardening-gate comment on MPE-74.
