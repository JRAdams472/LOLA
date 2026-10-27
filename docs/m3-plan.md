# LOLA M3 (MPE-67) — Control Plane, OpenIddict Identity & Tenant Provisioning

Identity + user/group management + tenant lifecycle, all control-plane
(non-PII-minimal). Four phases, one PR each; conforms to ADR-0002, -0003,
-0004, -0005, -0007, -0008, -0009, -0011 — no amendments needed.

## Ticket contract (MPE-67)

**Scope:** control-plane SQLite schema (tenant registry, users,
groups/roles, OIDC clients — auth identifiers only); OpenIddict server
with auth code+PKCE for both UIs and client-credentials for services over
**custom Dapper `IOpenIddict*Store`s** (no EF); user/group management with
roles `TenantAdmin`, `LoanOfficer`, `Borrower`, `Service` and
group→role→data-visibility (role decides masked vs plaintext); borrower
invite flow (single-use signed links, set-password, MFA-ready); tenant
provisioning job (tenant DB file → migrations → transit key → downstream
client creds); tenant-resolution middleware (one resolution per request,
no code path can name another tenant's file).

**Acceptance:** OIDC login flows work for both audiences + service
client-credentials tokens; provisioning creates a working isolated
tenant end-to-end; role→visibility map enforced in API responses;
isolation tests prove tenant A cannot reach tenant B data.

## Confirmed decisions (user-approved 2026-10-31)

| Question | Decision |
|---|---|
| M3/M4 data-plane boundary | **Minimal data plane now** — provisioning creates a SQLCipher-keyed file (file key wrapped by the tenant transit key, stored wrapped in control plane), runs a v1 schema marker, plus a request-scoped connection factory that can only open the resolved tenant's file. M4 adds rolling migrations, backup/restore, pooling. |
| Password hashing | **Argon2id** via `Isopoh.Cryptography.Argon2` (MIT, managed impl, PHC-string `Argon2.Hash`/`Verify` helpers) |
| Invite delivery | **`IInviteDelivery` port + dev impl** returning the single-use link in the API response; real delivery deferred to notification infra |
| Session model | **Rotating refresh tokens** with reuse detection for both UI clients |

Derived (consistent with existing conventions):

- Opaque reference access tokens + signed JWT id_tokens; the API
  validates via OpenIddict `UseLocalServer()` (same-process
  auth+resource server).
- Dev signing key: RSA generated once, persisted gitignored under
  `data/oidc/` (symmetric to `data/openbao/`); production key-source
  documented in runbook.
- OpenIddict 7.7.x stable (Apache-2.0, .NET 10-compatible; pin latest
  ≥7-days-old at implementation).
- Invite tokens: 256-bit opaque random (base64url), SHA-256 stored,
  single-use, 72 h TTL.
- Tenant-id charset (M2 hardening F1):
  `^[a-z0-9]([a-z0-9-]{0,61}[a-z0-9])?$` validated once in
  `TenantId.Validate`, used by provisioning AND `TransitKeyManager`/
  key-path call sites.
- `group_roles` may carry multiple roles; effective visibility = union,
  most-permissive wins.
- Masking enforced inside a Wolverine post-processor — REST and GraphQL
  both traverse it (ADR-0011).
- New `src/Lola.DataPlane` project for the tenant-file slice (mirrors
  `Lola.ControlPlane`; M4 expands it).
- Minimal dev login page at `/account/login` — placeholder until
  M11/M12 UIs; flows proven via scripted PKCE in tests.

## Architecture sketch

- `Lola.Domain`: reuse of `PiiClass`/`[PiiClass]`.
- `Lola.Application`: ports — `ITenantRegistry`, `IUserDirectory`,
  `IGroupDirectory`, `IInviteStore`, `IPasswordHasher`,
  `IInviteDelivery`, `IFieldVisibility`, `ITenantDbProvisioner`,
  `ITenantProvisioner`; `TenantContext` (AsyncLocal companion to
  `CipherContext`); Wolverine handlers for admin commands;
  `MaskingBehavior` post-processor.
- `Lola.ControlPlane`: migration `0002_identity.sql`; Dapper
  repositories for users/groups/invites/tenants.
- `Lola.DataPlane` (new): `TenantDbProvisioner` (SQLCipher file +
  wrapped file key + `schema_info`), `TenantConnectionFactory`
  (request-scoped, TenantContext-only).
- `Lola.Infrastructure`: `Identity/` —
  `LolaApplication/LolaAuthorization/LolaScope/LolaToken` POCOs + 4
  Dapper OpenIddict stores; `Argon2PasswordHasher`; `DevInviteDelivery`.
- `Lola.Api`: OpenIddict server + local validation, dev login endpoint,
  `TenantResolutionMiddleware`, `MaskingBehavior` registration, admin
  REST endpoints, stricter rate-limit partition on `/connect/token` +
  invite accept.

## Control-plane schema — `Migrations/0002_identity.sql`

- `ALTER TABLE tenants ADD COLUMN db_key_wrapped TEXT` (transit-wrapped
  SQLCipher file key); `db_path` stays.
- `users(id, tenant_id FK, email UNIQUE, display_name, password_hash
  PHC, status[active|disabled|invited], mfa_enabled, mfa_secret_ref
  NULL, created_utc, updated_utc)` — MFA-ready, not implemented.
- `groups(id, tenant_id FK, name, description)`;
  `group_roles(group_id, role CHECK IN TenantAdmin|LoanOfficer|Borrower|Service)`;
  `user_groups(user_id, group_id)`.
- `role_visibility(role, pii_class, access CHECK IN masked|plaintext)`
  — platform-wide seed map:
  - TenantAdmin → plaintext all classes
  - LoanOfficer → plaintext Name/Address/Contact/Income/Other;
    masked Ssn/Dob/Account
  - Borrower → masked all (own-data/reveal policy matures in M6)
  - Service → masked all (PII-free egress is M7's firewall anyway)
- `invites(id, tenant_id FK, email, role, token_hash UNIQUE,
  expires_utc, used_utc NULL, created_by)`.
- `openiddict_applications / _authorizations / _scopes / _tokens` —
  columns mirroring OpenIddict entity requirements (subject/status/
  type on tokens+authorizations; client_id+redirect_uris+permissions
  on applications).

## Phase 1 — Control-plane identity schema + provisioning service

Branch `MPE-67-phase-1`; ADR-0002 (DbUp append-only, Dapper),
ADR-0003 (per-tenant file), ADR-0004 (transit wrap).

- `TenantId.Validate` + apply in provisioning and `TransitKeyManager`
  key paths (M2 F1).
- Migration `0002_identity.sql` + repositories (`TenantRegistry`,
  `UserDirectory`, `GroupDirectory`, `InviteStore` — concrete in
  ControlPlane, ports in Application).
- `Argon2PasswordHasher` (Isopoh) — `Hash`/`Verify`, PHC strings.
- `TenantDbProvisioner` in new `Lola.DataPlane`: random 32-byte file
  key → `CipherContext.Use(tid)` → `IFieldCipher.EncryptAsync(b64key)`
  → `tenants.db_key_wrapped`; create `data/tenants/{id}.db` with
  `PRAGMA key="x'hex'"` FIRST then hygiene pragmas; `schema_info`
  marker table.
- `TenantConnectionFactory` — scoped; unwraps the file key once per
  request, opens keyed connections only for the `TenantContext`
  tenant (no tenant-id parameter exists on the type).
- `TenantProvisioningService` step pipeline: `RegistryStep` →
  `KeysStep` (`ITenantKeyManager.EnsureKeysAsync`) → `DataFileStep`;
  status transitions provisioning→active/failed; idempotent/
  re-entrant; `ClientCredentialsStep` plugs in Phase 4.
- Tests: tenant-id validation (incl. key-path rejection), Argon2
  round-trip + wrong-password, repo CRUD, provisioning e2e (row
  active, file exists keyed, transit keys exist, `db_key_wrapped` is
  `vault:` ciphertext), factory cannot reach a second tenant's file.
- Proof: `dotnet test` output + scripted sqlite probe showing the
  tenant file refuses unkeyed open.

## Phase 2 — OpenIddict server + custom Dapper stores

Branch `MPE-67-phase-2`; ADR-0005 (OpenIddict, custom stores, no EF),
ADR-0011 (REST ingress).

- Packages: `OpenIddict.AspNetCore` 7.7.x on Api +
  `OpenIddict.Core`/`Abstractions` on Infrastructure.
- `Lola.Infrastructure/Identity`: POCOs + `DapperOpenIddict{Application,
  Authorization,Scope,Token}Store` against `openiddict_*` tables.
- Server options: `/connect/authorize`, `/connect/token`,
  `/connect/userinfo`, `/connect/logout`, `/connect/revoke`; grants
  auth_code (PKCE required) + client_credentials + refresh_token;
  rotating refresh + reuse detection; passthrough on authorize/token/
  userinfo/logout so we emit `tid`/role claims and run our login.
- Validation: `AddValidation().UseLocalServer()` + `UseAspNetCore()`;
  opaque access tokens, signed JWT id_tokens; RSA dev signing key at
  `data/oidc/signing.json` (gitignored) generated on first boot.
- `OidcClientSeeder` hosted service via `IOpenIddictApplicationManager`
  (hashes secrets internally): `lola-officer-console` (code+PKCE,
  `http://localhost:3200/callback`), `lola-borrower-portal` (`:3300`),
  `lola-svc` (client_credentials, secret from `OIDC_SEED_SVC_SECRET`
  env — `.env.example` documents, never committed).
- Minimal `/account/login` GET form + POST (validates Argon2 hash →
  auth cookie → returnUrl); unauthenticated authorize requests
  redirect there.
- Tests (WebApplicationFactory): scripted PKCE flow (authorize→login→
  code→token→refresh→userinfo); client_credentials token +
  wrong-secret 401; refresh rotation + reuse-revocation; `tid`/role
  claims present in tokens.
- Proof: run logs of both flows + token claim dump; CI green.

## Phase 3 — Tenant resolution + authorization + role→visibility masking

Branch `MPE-67-phase-3`; ADR-0008 (masked-by-default), ADR-0003
(no cross-tenant path), ADR-0009 (no PII in logs).

- `TenantResolutionMiddleware` (after `UseAuthentication`): resolve
  `tid` claim → registry lookup (status=active) → `TenantContext` +
  `CipherContext.Use(tid)`; user tokens: `tid` must equal the user's
  tenant (claim minted from record); service tokens: `tid` scope
  validated against registered tenants. Unknown/inactive/mismatched →
  403 problem-details.
- Authorization policies for the four roles on `role` claims;
  `AuthenticationSchemes` = local OpenIddict validation.
- `IFieldVisibility`/`RoleVisibilityService`: union of group→role
  maps → per-class masked|plaintext.
- `MaskingBehavior` (Wolverine middleware): post-handler walk of
  result DTOs — `[PiiClass]` string members masked unless caller
  visibility grants plaintext (`•••` + last-4 when length permits);
  covers REST and GraphQL uniformly.
- First real enforcement surface: `GET /api/admin/tenants`,
  `GET /api/admin/tenants/{id}/users` (email `[PiiClass(Contact)]`),
  group CRUD, user↔group assignment, officer-user creation
  (TenantAdmin/Service only).
- Isolation tests: two tenants; A-token reaches A's data-plane rows;
  forged/mismatched `tid` rejected; TenantContext cleared between
  requests; factory API has no tenant-id parameter (compile-time) +
  traversal attempts rejected by `TenantId.Validate`.
- Masking tests: same users endpoint under TenantAdmin vs LoanOfficer
  vs Service tokens → plaintext/masked per map; GraphQL through the
  same handler masked identically.
- Proof: test output + captured JSON diff of masked vs plaintext.

## Phase 4 — Provisioning endpoint + borrower invites + e2e

Branch `MPE-67-phase-4`; ADR-0007 (delivery seam), invariant 7,
ADR-0011.

- `ClientCredentialsStep` — `svc-{tenant}` client_credentials client
  via `IOpenIddictApplicationManager`; secret generated, hashed by the
  manager, plaintext returned once in the provision response;
  `POST /api/admin/tenants` (Service/TenantAdmin) +
  `GET /api/admin/tenants/{id}` status.
- `IInviteDelivery` + `DevInviteDelivery` (returns link in response);
  `PORTAL_BASE_URL` env → `{base}/invite/{token}`.
- `POST /api/admin/tenants/{id}/invites` (LoanOfficer/TenantAdmin) →
  256-bit token, SHA-256 row, 72 h TTL → link.
  `POST /api/invites/accept` (public): token lookup → unused/unexpired
  → create Borrower user (Argon2) → mark used → 200.
- Rate limiting: ~10/min per-IP partition on `/connect/token` and
  `/api/invites/accept`.
- E2E test (marquee proof): provision tenant → admin creates officer
  → officer PKCE login → invite borrower → borrower accepts →
  borrower PKCE login → `tid` claim → borrower-scoped query hits only
  their tenant.
- `docs/identity-runbook.md`: client registration, dev signing-key
  rotation, invite ops, production key-source note. README refresh.
- Proof: full e2e run log + invite link transcript; CI green.

## Bookkeeping (Linear, per AGENTS.md)

1. Attach `docs/m3-plan.md` to **MPE-67** via signed-URL flow; move
   MPE-67 → In Progress.
2. Create 4 phase subtickets under MPE-67; mark each In Progress at
   phase start.
3. Per phase: PR (one per subticket) → proof comment 1:1 vs
   acceptance + code-shape report → pause for merge approval.
4. Close-out: hardening-gate comment (OWASP + app hardening +
   ADR-drift) on MPE-67 → Done.

## Verification

- Per phase: `dotnet format --verify-no-changes` / `build` / `test`
  (+ coverage floor via covercheck); `docker compose up --build`
  smoke; gitleaks.
- New test surface: `Lola.ControlPlane.Tests` (schema/repos),
  `Lola.DataPlane.Tests` (provisioning/factory),
  `Lola.Infrastructure.Tests` (stores, hasher), `Lola.Api.Tests`
  (OIDC flows, middleware, masking, invites, isolation e2e).
- Coverage floor 80 %/module maintained (goal 85 % overall).

## Risks / honest limitations

- **OpenIddict custom stores are the heavy lift** — ~20-method
  interfaces ×4; Phase 2 is sized for it.
- **Dev-only:** RSA signing key in `data/oidc/` (gitignored), invite
  link returned not emailed, opaque tokens require same-app
  validation (introspection for external validators = later ticket),
  no token pruning job (OpenIddict.Quartz deferred — file follow-up).
- Control-plane DB stays unencrypted at file level (holds only hashes
  + auth identifiers + wrapped keys); flag for hardening-gate review/
  M4 evaluation.

## Out of scope (owned elsewhere)

Rolling tenant migrations/backup/restore (M4), audit/reveal event
pipeline (M6), PII-free egress firewall (M7), real email delivery,
real login UIs (M11/M12), MFA implementation (schema-ready only),
per-tenant role-visibility overrides.
