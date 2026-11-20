# M19 — TPO sub-tenancy + pre-approval letters (MPE-247)

Lodasoft-style third-party origination: lenders invite external partners
(brokers, realtors) into their **own tenant boundary** with row-scoped
loan visibility — stricter than borrower scoping, far stricter than
staff. No cross-tenant federation (megaplan non-goal).

Conforms to **ADR-0003** (link rows live in the tenant file),
**ADR-0005** (new `LolaRole` values ride the existing
group→role→visibility machinery), **ADR-0007** (letters go through the
`IDocumentService` seam), **ADR-0008** (partners get *zero* plaintext
grants — `role_visibility` absence = masked).
Invariants: **8** (masked default, reveal impossible for partners),
**5** (OIDC/TLS), **4** (every partner action audited with
external-actor attribution), **1** (no new plaintext surface).

## Design

### Roles — `Broker` + `Realtor`

`LolaRole` gains two values. Everything else is existing machinery:

- `invites.role` CHECK constraint currently lists the four locked
  roles — control-plane migration rebuilds the table with the extended
  CHECK (SQLite can't alter CHECKs; same rebuild pattern DbUp used for
  earlier constraint changes).
- `role_visibility` needs **no rows** for partner roles — absent rows
  are masked (ADR-0008), so partners are maximally masked by
  construction. `RevealPii`, `TenantStaff`, `AdminRead`, `PlatformOps`
  policies are unchanged — partners are simply never in them.
- New `PartnerAccess` policy = `Broker` + `Realtor` for partner-only
  endpoints. `Applications` policy gains both roles so shared
  endpoints (uploads, conditions, documents) admit partner tokens —
  the *handler* scope check is the enforcement, same shape as
  borrower scoping today.
- Accept-invite derives the tenant group from the invite role:
  `brokers` → `[Broker]`, `realtors` → `[Realtor]` (lazy-created like
  `borrowers` today).
- `users.org_name` (nullable, control plane) captures the TPO org on
  the invite → user — audit/attribution only, not an authorization
  input.

### Row-scoped visibility — `application_partners` (tenant DB, `0017`)

```sql
application_partners(
  application_id  TEXT NOT NULL REFERENCES applications(id),
  partner_user_id TEXT NOT NULL,
  partner_role    TEXT NOT NULL CHECK (partner_role IN ('Broker','Realtor')),
  linked_by       TEXT NOT NULL,
  linked_utc      TEXT NOT NULL,
  revoked_utc     TEXT,
  PRIMARY KEY (application_id, partner_user_id))
```

- Staff link/unlink endpoints (`TenantStaff` policy):
  `PUT/DELETE /api/tenants/{t}/applications/{id}/partners/{userId}` —
  writes `loan_events` (`partner_linked`/`partner_revoked`) + tenant
  audit. Revoke is a soft-delete (`revoked_utc`) so the audit trail
  keeps the access window.
- `RequireAppAsync`'s `isBorrower` boolean becomes an actor scope:
  `Staff` | `Borrower(userId)` | `Partner(userId)`. Partner scope
  resolves through `application_partners` — no live link →
  `DomainException.Forbidden` (same 403 shape as borrower scoping).

### Scoped surfaces

- `GET …/applications` — partner actors see only linked apps;
  `PartnerApplicationDto` = meta + status + step + loan amount with
  **every PII class masked** (partner roles have zero visibility
  grants — the masker does the work, no special-casing).
- `GET …/applications/{id}` — linked-partner read: same masked
  projection, never the staff document payload.
- Uploads — partner `POST …/uploads` on linked apps reuses the M10
  pipeline unchanged (quarantine → scan → classify); `uploaded_by`
  already stamps the actor. Partner `GET …/uploads` is filtered to
  `uploaded_by == self` — partners never see borrower or officer docs
  ("own-upload docs only" per ticket).
- Conditions — linked-partner conditions read reuses the M15
  `BorrowerConditionDto` projection (already stripped: no actor, no
  internal linkage, no classification detail).

### Pre-approval letters

- New doc kind `preapproval-letter` on the existing
  `RequestDocumentPackage` → doc-outbox → `IDocumentService` path
  (ADR-0007). `DisclosurePayloadBuilder` gains a minimal-payload
  builder for the kind — only fields the letter template requires
  (borrower name, requested amount, expiry, lender identity);
  **not** the application document.
- `POST …/applications/{id}/preapproval-letters` — staff (any app) or
  partner (linked app only, `PartnerAccess`). Partner triggers write
  `preapproval_requested` events + audit with external-actor
  attribution (`partner:` actor prefix convention).
- Download via the existing `GET …/documents` + presigned-capability
  flow — letters appear in the tenant `documents` table like any
  generated package; partner read is scoped to letters on linked apps.

### Portal surface — partner area in `clients/portal`

Reuse, don't rebuild: `/auth/me` already returns roles — a partner
session lands on `/partner`:

- Linked-loan list (masked name, ref, status, step)
- Detail: status, open "what we need" conditions, upload widget
  (same allowlist/bounds), own uploads, request-letter button
- No admin, audit, reveal, pricing, or staff surfaces — the routes
  simply don't exist for the role, and every API call is server-side
  scope-checked anyway (UI is convenience, never the boundary).
- BFF path allowlists gain the partner routes.

## Phases

| Phase | Ticket | Branch | Scope |
|---|---|---|---|
| **1 — identity + link plumbing** | MPE-265 | `MPE-265-phase-1` | `LolaRole` +2; CP migration `0008_tpo.sql` (invites CHECK rebuild + `users.org_name`); DP `0017_application_partners.sql`; partner invites (TenantAdmin) + accept→`brokers`/`realtors` groups; `ActorScope` refactor of `RequireAppAsync` + partner link check; staff link/unlink endpoints + events/audit; `PartnerAccess` policy; `Applications` policy +2 roles |
| **2 — scoped surfaces + portal** | MPE-266 | `MPE-266-phase-2` | Partner-scoped list/detail (masked `PartnerApplicationDto`); partner upload (linked apps) + own-uploads-only list; partner conditions read; `/partner` portal area (list, detail, upload, own docs); BFF allowlist; cross-link 403 + mask tests |
| **3 — pre-approval letters** | MPE-267 | `MPE-267-phase-3` | `preapproval-letter` kind + minimal payload builder; trigger endpoint (staff + linked partner); outbox→ISPPr; doc row + presigned download; `preapproval_requested` events + external-actor audit |
| **4 — marquee + gate + close-out** | MPE-268 | `MPE-268-phase-4` | Live marquee (invite→accept→link→scoped reads→cross-link 403→upload→letter→audit trail); portal partner screenshots; README/wiki; M19 hardening gate on MPE-247; code-shape; close |

## Acceptance mapping (MPE-247 bullets)

- *Broker sees only linked loans; cross-link → 403* — P1 link check +
  P2 scoped queries; marquee + endpoint tests.
- *Masked-by-default for partners; no reveal* — zero `role_visibility`
  grants + `RevealPii` untouched; tests assert masked projection and
  403 on reveal.
- *Letter via ISPPr minimal payload + presigned download* — P3;
  payload-builder test asserts field list; marquee end-to-end.
- *Partner actions audited, external-actor* — events + audit on
  link/unlink/upload/letter; marquee reads them back.

## Risks / notes

- **The whole milestone is broken-access-control** — every partner
  route must fail closed on scope, and tests assert 403, not absence.
- `ActorScope` touches every `RequireAppAsync` call site (~15) —
  mechanical, but the diff crosses many handlers; keep it signature-
  only, no behavior change for staff/borrower paths.
- ISPPr needs a `preapproval-letter` template on their side; the stub
  adapter proves our pipeline regardless — flag to the sibling team
  via the existing contract-doc convention if their template lags.
- Hardening gate on MPE-247 at close per AGENTS.md.
