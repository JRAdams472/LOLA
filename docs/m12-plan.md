# M12 (MPE-76) — Borrower portal plan

Build the LOLA borrower portal at `clients/portal` (Next.js 16 + Tailwind,
GitHub-dark, BFF token custody — same pattern as the M11 officer console):
invite acceptance → account setup, MISMO-section intake wizard with
save-draft + resume, borrower-safe status tracker, vault document upload,
disclosure/package download, masked-confirm PII display, short-lived
sessions with idle timeout.

## Context & decisions confirmed

Milestone **M12 / MPE-76** — the borrower-facing experience. Depends on
M5, M9, M10 — all merged; M11 shipped the officer console this portal
complements. Decisions for this plan:

- **Separate app, same auth architecture** — `clients/portal` reuses the
  proven `clients/web` pattern (auth-code PKCE → sealed httpOnly JWE
  session cookie → BFF proxy attaches the bearer; browser never sees a
  token) but is its own deployable on `:3300` — public ingress stays
  isolated from the internal console. The `lola-borrower-portal` OIDC
  public client is already seeded (PKCE required, `:3300/callback`).
- **Tailwind per the ticket** ("revisable" noted — kept): the portal is
  a different visual product than the dense internal console — calmer,
  guided, lower-density. `lib/` auth/BFF/nav code adapts from
  `clients/web`; MUI/React Query do not carry over — plain fetch +
  server components keep the surface small. (Flag: swapping to MUI for
  velocity is a viable plan-review override, not a drift — it's a stack
  detail, not an ADR.)
- **Portal reads via GraphQL per ADR-0011** — a `portalLoan` field
  returning a dedicated borrower-safe DTO, and nothing else. Mutations
  (draft PATCH, submit, upload, download mints) stay REST — same split
  as the console, and file up/download stays REST per the ADR.
- **Borrower-safe read projection, not reuse of `ApplicationDto`** —
  `ApplicationDto` carries `PipelineStep` (`POSIntake`/`PPE`/`Fee`…),
  `CurrentVersion`, `BorrowerUserId`, `TridClockStart` — internal
  surface the ticket forbids exposing. A new `PortalLoanDto` projects
  exactly what the portal needs: friendly status stage, masked doc,
  documents, uploads, friendly deadlines. Acceptance bullet "no
  officer-only fields or internal status values" is proven by a
  contract test asserting the payload key set and status vocabulary.
- **Masked-confirm is already the data-plane default** — the seeded
  `role_visibility` map grants Borrower zero plaintext classes, so
  `•••`+last-4 (e.g. SSN `•••6789`) is what every read produces: the
  borrower confirms their own values without ever seeing them in full.
  The portal never calls `/reveal` (staff-only `RevealPii` policy).
- **"MFA-ready"** — `users.mfa_enabled` exists and the portal auth flow
  is structured so a factor step can slot between password verify and
  session issue. TOTP itself is NOT this milestone (filed as a
  follow-up; the column + flow seam is the "ready" deliverable).
- **Session hardening** — portal sessions get a shorter ceiling than
  the console: sliding **idle timeout** (`PORTAL_SESSION_IDLE_MIN`,
  default 30 min, enforced via last-activity timestamp in the sealed
  session) plus an absolute bound on the refresh chain. Same Secure/
  HttpOnly/Lax cookie posture; `next`/`returnUrl` sanitization and BFF
  path guards carry over verbatim from `clients/web/lib`.

## API surface consumed (exists today — M3/M5/M9/M10/M11)

| Portal need | Endpoint / handler |
|---|---|
| Invite accept (public) | `POST /api/invites/accept` `{token, displayName, password}` — single-use 72h token, `auth-tight` limit, creates Borrower user via `borrowers` group |
| OIDC login | `lola-borrower-portal` public client — PKCE required, `:3300/callback`, `openid profile offline_access lola-api` |
| Own loans list | `ListApplicationsQuery` — already filters `BorrowerUserId == actor` for borrowers |
| Own draft read (masked) | `GET …/applications/{id}` → masked `ApplicationDto` (superseded by portal projection — see backend work) |
| Draft save/resume | `PATCH …/applications/{id}` — own-draft guarded, atomic merge (MPE-216) |
| Submit | `POST …/applications/{id}/submit` — own-draft guarded; 422 `FieldError[]` for the wizard's correction loop |
| Doc upload | `POST …/applications/{id}/uploads` (multipart) — borrower-own allowed; magic-byte allowlist pdf/png/jpg/tiff/docx, ≤25 MB, ClamAV quarantine→clean |
| Upload list/download | `GET …/uploads[/{docId}[/download]]` — borrower-own; download mints ≤15-min capability URL (302) |
| Disclosure download | `GET …/documents[/{docId}[/download]]` — borrower-own; presigned 302 |
| Refused by design | search, transition, correct, price/fees/packages, reveal — all reject `ActorIsBorrower` in handlers already |

Rate limiting: `intake` on the applications group, `auth-tight` on
invite accept + token endpoints — already in place.

## Known backend work (in scope)

1. **`GetPortalLoanQuery` + `PortalLoanDto`** — borrower-scoped composite
   read: `{ id, statusLabel, statusStage, statusStages[], doc (masked),
   documents[], uploads[] }`. `BorrowerStatusMap` translates
   `status`/`pipeline_step` internals to borrower vocabulary —
   `draft→"Application in progress"`, `submitted/in_review→"Under
   review"`, `decisioned→"Decision ready"`, `disclosures→"Disclosures
   ready"` etc. No `pipeline_step`, versions, actor ids, TRID internals,
   lock data, or staff fields in the payload. Own-app guard reuses
   `RequireAppAsync`'s borrower scope.
2. **GraphQL `portalLoan`/`portalLoans`** — thin resolvers over the
   portal queries (ADR-0011); the officer `application(s)` fields stay
   off-limits to Borrower via existing handler guards, but the
   contract test pins it anyway.
3. **Portal session config** — `PORTAL_SESSION_IDLE_MIN` +
   `PORTAL_SESSION_MAX_H` read by `clients/portal/lib/auth.ts`; idle
   enforced by touching a last-activity timestamp inside the sealed
   session on each BFF call; expired → bounce to login.
4. **Compose `web-borrower`** service on `:3300` (commented slot
   exists), `Dockerfile.portal` or shared multi-target web image,
   `CORS_ORIGIN` addition, `.env.example` portal block.

## Phases (one subticket + `MPE-<n>-phase-<m>` branch + one PR each)

### Phase 1 — `clients/portal` scaffold, invite acceptance, hardened session
- `clients/portal`: Next.js 16 App Router + React 19 + TS + Tailwind 4;
  eslint flat config, jest + Testing Library, playwright config —
  toolchain pinned per the ≥7-day dependency rule.
- `lib/auth.ts` adapted from `clients/web`: PKCE+state cookies,
  authorize→token exchange, sealed `lola_portal` JWE cookie,
  **idle-timeout + absolute bound** (backend item 3), refresh dedup.
  Routes: `app/auth/login`, `app/callback`, `app/auth/logout`,
  `app/auth/me`. `lib/nav.ts`/`lib/bff-paths.ts` carried over verbatim
  (the hardened versions from the M11 gate).
- `app/bff/[...path]/route.ts` — same-origin proxy, per-route body
  caps (2 MB default, ~26 MB streamed `duplex` only for `/uploads`).
- `/accept?token=` — invite acceptance page: token from query,
  displayName + password (strength rules mirrored from
  `InputValidation`), success → login. `/login` bounce middleware.
- Shell: Tailwind GitHub-dark theme, minimal shell (identity chip,
  sign-out), `PRODUCT.md` via impeccable init + `DESIGN.md` (borrower
  product voice: guided, reassuring, plain-language).
- Compose `web-borrower` :3300 + Dockerfile + `.env.example`.
- Tests: auth/session unit tests incl. idle-expiry, accept-page spec,
  playwright auth setup.
- ADRs 0005/0011; invariants 5, 7.

### Phase 2 — Portal read surface + status tracker + dashboard
- Backend items 1–2: `GetPortalLoanQuery`, `PortalLoanDto`,
  `BorrowerStatusMap`, `portalLoan`/`portalLoans` resolvers.
- Contract test: portal payload contains no `pipelineStep`,
  `currentVersion`, `borrowerUserId`, `trid*` internals; status
  vocabulary is the borrower map only; other-borrower's id → 403.
- `/` dashboard: loan card(s) — status tracker (stage progress,
  friendly copy), next-action hints, docs/upload summary links.
- Tests: resolver masking/field-set tests, status-map unit tests,
  dashboard spec.
- ADRs 0008/0011; invariants 3, 8.

### Phase 3 — Intake wizard (MISMO sections, save-draft + resume)
- `/apply`: sectioned wizard over the field catalog — Borrower info /
  Employment & income / Assets / Liabilities / Property & loan /
  Review — reusing the generated field-map mechanism
  (`scripts/gen-field-map.mjs` → portal `lib/field-map.ts`).
- Save-draft on section nav + explicit save (PATCH merge); resume
  loads masked values (`•••`+last4 confirm for PII inputs — edit to
  replace, empty stays empty).
- Review step → `POST /submit`; 422 `FieldError[]` maps onto wizard
  fields with section deep-links (the correction loop, borrower-side).
- Progress persistence: partial draft survives logout/login (resume).
- Tests: masked-confirm render, save/resume round-trip, 422 mapping,
  playwright wizard spec.
- ADRs 0006/0008; invariants 1, 6, 8.

### Phase 4 — Documents: upload + disclosure download
- `/documents`: upload picker (classification select → allowlist
  enum: paystub, w2, bank-statement, id, other), multipart through
  BFF with streamed body + caps; status chips
  (quarantined/clean/infected) with friendly copy; retry on reject.
- Disclosures/packages list + download (follow 302 → presigned);
  uploads list + own download.
- Empty/pending states; plain-language explainer ("why we ask").
- Tests: upload spec incl. oversize/disallowed rejection paths,
  download spec, e2e upload→clean→listed.
- ADR-0007 (browser never touches MinIO/ISPPr — all via LOLA/BFF),
  0008; invariants 1, 3.

### Phase 5 — Marquee e2e + milestone hardening gate + close-out
- Playwright portal marquee on the real stack: admin creates invite
  → accept → portal login → wizard fill → save/resume → submit →
  status tracker → upload → disclosure download; screenshots on the
  subticket; session idle-expiry spec.
- **Hardening gate (required before MPE-76 closes):** OWASP review of
  the *public* portal surface — invite-token handling (hash-only
  storage, single-use, TTL, rate limit), session cookie flags + idle
  enforcement, open-redirect on `next`/`returnUrl`, BFF path guards,
  upload caps + magic-byte sniff, GraphQL depth/cost on the public
  resolver, no-officer-fields contract re-verified, Seq zero-PII from
  e2e runs, dep freshness; ADR-drift review; findings comment on
  MPE-76.
- Close-out: code-shape report, README M12 section, wiki
  Borrower-Portal page + lolashots screenshots (synthetic borrowers),
  branch cleanup, follow-up tickets.

## Acceptance map (MPE-76)

| Ticket acceptance | Proof |
|---|---|
| Borrower completes intake end-to-end from invite link | P1 accept flow + P3 wizard + P5 marquee spec (invite→submit) |
| Uploads land in vault; disclosures downloadable; session expiry enforced | P4 upload/download specs + P5 marquee; idle-timeout unit + e2e spec |
| No officer-only fields or internal status values in borrower API responses | `PortalLoanDto` contract test (key-set + status vocabulary pin) + resolver tests |

## Risks / honest flags

- **GraphQL on a public surface** — the `portalLoan` resolver widens the
  public ingress beyond REST; depth-8/cost-300 guards already enforced
  milestone-wide, and the resolver returns a fixed DTO (no field-level
  drift). Noted in the P5 gate scope either way.
- **Masked-confirm UX** — borrowers re-editing a masked field must
  clear-then-type, not edit the mask; the wizard treats `•••` values as
  display-only and never round-trips them into a PATCH (tested — a
  round-tripped mask would corrupt the stored value).
- **Resume reads masked PII** — correct per ticket (borrower confirms,
  doesn't view); the wizard needs care that "unchanged" and "empty"
  are distinguishable in the draft doc (non-PII fields read plaintext,
  PII confirm-by-replace).
- **Tailwind ramp** — first Tailwind client in the repo; the console's
  component vocabulary doesn't port, so P1 carries a slightly larger
  design-system cost than M11 P1 did.
- **Dev invite delivery** echoes the link in the API response — the
  portal accept flow assumes `PORTAL_BASE_URL`-rooted links; seeded
  dev + e2e both use `http://localhost:3300`.

## Workflow notes (per AGENTS.md)

Plan doc lands in `docs/m12-plan.md` and is attached to **MPE-76**
before phase subtickets are cut; MPE-76 → In Progress at Phase 1 start.
One PR per subticket, green gates (`dotnet format/build/test`, portal
lint/typecheck/test/build), proof comment + pause for merge approval
after every PR. Milestone ends only with the hardening-gate comment on
MPE-76.
