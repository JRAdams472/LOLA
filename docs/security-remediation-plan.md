# LOLA security remediation plan

Status: phases 1–9 implemented and merged (MPE-271…MPE-290); phase 10
release/recovery gate in flight under MPE-279. Based on the LOLA review
at `62f5516`.
This is not approval to change an ADR, migrate a deployment, or mark a finding fixed.
Scope is LOLA only: its API, clients, stores, deployment configuration and tests.
Do not inspect or modify sibling projects. Test integration contracts at LOLA's
adapter boundaries; keep existing CI checks and live suites enabled.

## Sequencing and ownership

| Phase | Deliverable | Depends on | Gate |
|---|---|---|---|
| 1 | Separate fleet and tenant authority | None | Before MPE-266 |
| 2 | Enforce operation permissions on both ingress paths | 1 | Before MPE-266 |
| 3 | Bind invites to protected roles; accept atomically | 1, 2 | Before MPE-266 |
| 4 | Scope queries before decryption; narrow partner DTOs | 1-3 | Backend portion of MPE-266; before exposing partner UI |
| 5 | Commit access changes with audit evidence; constrain reasons | 2 | Before real data |
| 6 | Revalidate privileges and bound session revocation | 1-3 | Before real data |
| 7 | Protect the control plane, including migration and recovery | 3; coordinate with 6 | Before real data |
| 8 | Separate production startup from development defaults | Design can start now; final checks need 6-7 | Before production deployment |
| 9 | Resolve BFF token custody and implement approved session design | 6-8 and custody decision | Before production deployment |
| 10 | Run the combined release and recovery gates | All applicable phases | Release sign-off |

Phases 1-3 are the smallest safe checkpoint for resuming M19: a staff-only
portal is not a substitute for protecting the API. Phase 4 is **not** a second
partner-portal ticket. Fold its acceptance into MPE-266. MPE-267 adds exactly
one letter-sharing capability; do not pre-enable it in phase 2. MPE-268 still
owns the M19 marquee and milestone gate.

**Handoff for each implementer.** Read `AGENTS.md`, this phase, `docs/m19-plan.md`,
the listed ADRs and the full assigned Linear issue before editing. At scheduling,
create or identify the LOLA remediation parent, attach this plan, then create
phase subtickets (reuse MPE-266 for phase 4). The numbers above are plan IDs,
not issued tickets. Use one ticket-prefixed branch and PR per subticket; pause
for merge approval. If a phase needs several PRs, split it into dependent
subtickets first. Recheck `main`, migration numbers, routes, and all callers
at phase start. Paths below are repo-relative; bare type names are search keys.

**Shared fixture and evidence.** In `tests/Lola.Api.Tests`, extend the existing
`LolaApiFactory`/PKCE fixtures with two synthetic tenants, two applications in
one tenant, and a second tenant with disjoint IDs. Use actual OIDC principals
for endpoint proof; mocks are appropriate for counting calls and injecting
failures. Use disposable stores and non-destructive spies for lifecycle denials.
No real identities, bearer tokens, keys, presigned URLs or PII in evidence.
Do not run erase, restore, rekey, or a negative authorization probe on a live
tenant. In every phase run the named focused suites first, then the common
PR checks at the end of this plan.

## Phase 1 - Reserve platform operations for platform identities

**Start:** `src/Lola.Api/Identity/{ConnectEndpoints,LolaPolicies,OidcClientSeeder}.cs`,
`src/Lola.Api/Admin/AdminEndpoints.cs`, `PlatformOpsTests` and
`ProvisioningAndInviteTests`. ADR-0003/0005. Preserve designated global
automation; do not remove `Service` everywhere to close one overly broad policy.

1. Enumerate every use of `PlatformOps` and every unscoped registry operation.
   Inventory list/create, fleet migration, backup/list/restore, erasure and
   template republish. Separately record the existing own-tenant status rule.
2. Issue a dedicated platform permission only in the validated client-credentials
   grant for explicitly designated global client registrations. Preserve the
   current positive cases for `lola-ops` and `lola-svc`; bind the grant to
   trusted client identity, not caller-requested scopes, role names or group
   membership. Tenant service grants retain their tenant ID and tenant permissions.
3. Change `PlatformOps` to require that permission and reject a tenant-bound
   principal even if conflicting claims exist. Prevent group creates/updates
   from assigning machine/platform authority to humans. Reject unknown or
   numeric role values. Inventory pre-existing invalid grants and require
   operator reconciliation; do not silently broaden them or erase history.
4. Apply the policy to the entire inventory. Keep `GuardedStatus` and data-plane
   checks tenant-bound for tenant principals. Document that old platform tokens
   must be reissued at cutover; never fall back to accepting a bare `Service` role.
5. **Prove:** parameterize the fleet inventory for global ops, global automation,
   tenant service A, tenant admin A, ordinary user and anonymous. For forbidden
   calls assert 403 (401 unauthenticated) *and zero lifecycle handler calls*.
   Verify tenant service A still reads allowed A status but not B. Use spies,
   not real restore/erase jobs, even for permitted cases.

## Phase 2 - Separate permission to read a row from permission to use an output

**Start:** `LolaPolicies.ScopeFor`, `ActorScope`, `ApplicationEndpoints`,
`src/Lola.Api/GraphQL/Query.cs`, `ExportMismoHandler`, `ExportUlddHandler`,
`GetApplicationDocumentsHandler`, `GetDocumentDownloadHandler`, directory and
group handlers. ADR-0005/0007/0008/0011.

1. Build a checked-in operation matrix in the API tests. Start with the rows
   below; expand it from actual REST mappings, GraphQL resolvers and handler
   call sites. Include anonymous, no recognized role, mixed roles, tenant
   service, staff, borrower, Broker and Realtor. For scoped operations vary
   tenant, ownership, active link, revoked link, uploader and document kind.

   | Operation | Required decision |
   |---|---|
   | Admin directory, groups, membership | Same `AdminRead` decision via REST and GraphQL; enforce matching tenant |
   | Application metadata | Staff within tenant, borrower-own, partner-linked; purpose-specific projections |
   | Plaintext MISMO export | Explicit export permission; preserve authorized staff and borrower-own; never infer from a partner link |
   | ULDD export, reveal, audit and workflow detail | Explicit existing operation policy; partners denied; `Service` is not automatically a human officer |
   | Package list/detail/download | Preserve staff and borrower-own contracts; partners denied existing kinds, including completed ones |
   | Vault list/detail/download | Partner requires active link **and** own upload; clean-only for download |
   | Pre-approval letter | No partner grant now. MPE-267 must explicitly permit kind + operation + active link |

2. Introduce one trusted current-actor/permission evaluator usable by both
   ingress adapters and application handlers. Derive subject, tenant, identity
   kind and roles from authenticated context; never accept them from a request
   DTO. Keep row scope separate. Change `ScopeFor` and default/unrecognized
   enum handling to deny, not `Staff`; reject missing subject. Document mixed
   external/staff role behavior; fail closed on ambiguous combinations until
   explicitly approved. Make non-interactive worker authority explicit.
3. Put operation checks before document loads, decryption, audit reads and
   URL signing. Cover all three package reads as well as exports. Review other
   handlers accepting `ActorScope` for a row-only check on a privileged output.
   Until phase 4 is merged, deny partner detail or return only a minimal
   allowlisted projection; do not temporarily expose the staff document.
4. Apply the shared decision to `TenantUsers`, `TenantGroups`, `GroupMembers`
   and each protected resolver. Masking is a second control, never a replacement
   for authorization. Keep intended public health/ping routes explicit. For
   GraphQL authorization errors, assert the configured forbidden error and
   absence of protected field data, not an HTTP status alone.
5. **Prove:** extend `PartnerTests`, `TenantAccessTests`, `GraphQlTests` and
   `InteropEndpointTests`. In particular use a **linked** partner, a populated
   synthetic document and a **completed** package: require denial with no
   XML, redirect, signer call or directory data. Re-run legitimate borrower
   export and staff reads. Add a matrix entry requirement when adding a new
   protected route/resolver. No partner `role_visibility` rows.

## Phase 3 - Make invite acceptance a single control-plane operation

**Start:** `AcceptInviteHandler`, `CreateInviteHandler`, `GroupDirectory`,
`InviteDirectory`, `UserDirectory`, admin group mutations and
`src/Lola.ControlPlane/Migrations/`. ADR-0002/0005. No change to token delivery.

1. Add an append-only migration for a stable per-tenant system-group identity
   (for example, unique `(tenant_id, system_kind)` for borrower/broker/realtor).
   Backfill an existing named group only when its role set is exactly the
   expected singleton. For a conflict, report opaque IDs and block acceptance
   until reviewed; never silently promote members or create a privileged alias.
2. Protect system identities against ordinary rename/reassignment, deletion
   and role edits; prevent custom groups from claiming reserved identities.
   Check both admin ingress and the shared mutation boundary. Keep explicit
   membership administration separate from invite-derived grants.
3. Add a narrow application port for transactional acceptance and implement it
   with Dapper on **one** control-plane connection/transaction. After basic
   validation, compute the password hash outside the SQLite write lock. Inside
   the transaction recheck token hash, unused state, expiry, tenant and duplicate
   identity; resolve/create the protected group, verify its role set, insert the
   user and membership, conditionally consume the invite, and commit. Any
   failed predicate, uniqueness race or storage error rolls back all writes.
4. In `ControlPlaneResiduals`, clear `org_name` with name/email/credential
   cleanup. Extend `ResidualCleanupTests` with an invited partner bearing a
   synthetic org. Keep the existing audit tombstone and retention gate intact.
5. **Prove:** use migrated SQLite, not only fake directories. Two concurrent
   accepts of one invite yield one complete user/membership and one rejection.
   Two different first invites of the same kind converge on one system group.
   Wrong-role legacy group and injected failure before commit leave no new
   identity. PKCE login after successful accept yields exactly the intended role.
   **M19 restart gate:** phases 1-3 merged, negative matrix green, no unreviewed
   system-group conflicts in the intended environment.

## Phase 4 - Scope application reads before decryption (MPE-266)

**Start:** `ListApplicationsHandler`, `UpdateDraftHandler.RequireAppAsync`,
`src/Lola.DataPlane/ApplicationRepository.cs`, `GetApplicationHandler` and
the vault/portal read handlers. ADR-0002/0003/0006/0008/0011.

1. Replace list-all-then-filter for external actors with parameterized repository
   queries scoped by borrower ID or active partner link. Put predicates before
   paging and before fetching party ciphertext. Use a bounded page size and
   deterministic secondary ordering; coordinate client/API paging contracts.
2. For detail, authorize using minimal metadata before opening the MISMO payload.
   Where possible make the scoped read one query/transaction to avoid a gap
   between checking a link and consuming it. Preserve M19's 403 contract for
   denied external actors without disclosing whether an inaccessible row exists.
3. Return a dedicated allowlisted partner DTO per `docs/m19-plan.md`; no staff
   JSON, rate-lock internals or document intelligence. If the UI needs a masked
   label, prefer an opaque label over decrypting a name just to hide it. Apply
   uploader ownership in vault queries; use the stripped conditions projection.
4. **Prove:** seed linked/unlinked/revoked and cross-tenant rows across page
   boundaries. With a recording cipher assert no unrelated name/document is
   decrypted; assert serialized DTO keys, not just masked sample strings.
   Verify borrower-own and staff regressions, and deny immediately after unlink.
   The MPE-266 portal consumes this contract; UI filtering is not a control.

## Phase 5 - Make grants, revocations and evidence agree

**Start:** `LinkPartnerHandler`, `UnlinkPartnerHandler`, `ApplicationPartnerRepository`,
`TenantAuditLog`, `LoanEventLog`, `LoanEventWriter`, `RevealApplicationHandler`,
and `EraseTenantHandler`. ADR-0002/0003/0008/0009.

1. Add a tenant write port for link/revoke plus evidence. Reuse the existing
   transaction-aware event append pattern. Provide audit append on that **same**
   connection and transaction; compute each chain predecessor inside the write
   transaction. Do not nest independent repository transactions.
2. Commit the link state, loan event and audit entry together. Define repeated
   link, repeat revoke and re-link behavior; preserve documented status codes.
   A retry must not produce a second successful grant event for a no-op.
3. Replace unbounded operator reasons on reveal and retention override with
   validated reason codes; inventory other audit writers for free text and
   query-string transport. Update both clients/contracts in the same delivery.
   Reject unknown codes rather than logging input. If narrative is legally
   required, classify and encrypt it separately and audit its reference.
   Never rewrite historical hash-chained rows to sanitize them; classify any
   legacy exposure and handle it through an approved retention process.
4. **Prove:** in an isolated tenant DB inject failures on event and audit append
   separately. Reopen the DB: either all three effects exist or none do.
   Verify both hash chains after retries and concurrent writes. Pass a synthetic
   PII marker as an invalid reason; verify it is absent from audit, events,
   response details and captured telemetry. Retention override remains attributable.

## Phase 6 - Revoke stale human authority across token and cookie flows

**Start:** `ConnectEndpoints`, group/user mutation handlers, tenant resolution
middleware, the Dapper OpenIddict stores and `OidcFlowTests`. ADR-0002/0005.

1. Define the contract: after a committed disable or privilege reduction, the
   **next protected request** may not exercise the old authority. An already
   running request is not recalled. Existing presigned capabilities are a
   separate contract: document their bounded TTL and decide explicitly whether
   a new capability type needs online revocation.
2. Add a per-user authorization version. Bump it transactionally on disable,
   password/security reset, membership changes and role edits affecting existing
   members (including group deletion). Use stable user and tenant IDs, not email.
3. Stamp the version into issued human grants and login sessions. On cookie
   authorization, code redemption and refresh, check active user/tenant and
   current version before issuing tokens; require fresh login on a stale
   grant. Check version for protected bearer requests too, or use a proven
   equivalent immediate reference-token revocation mechanism. Do not leave
   a 30-minute access-token exception. Missing legacy version requires re-login.
4. Preserve service-client flows and refresh reuse detection. Coordinate token
   revocation with logout and phase 9 session invalidation. Fail closed if current
   authority cannot be established. Do not cache a positive decision beyond
   the agreed deadline.
5. **Prove:** mint a real cookie, access token and refresh token; downgrade one
   synthetic user, then check all three paths without a new login. Repeat for
   disable and group-role changes. An unaffected user and tenant service still
   work. Test failure/unavailability of the authority store.

## Phase 7 - Protect control-plane identities and their recovery path

**Start:** `ControlPlaneDb`, `ControlPlaneMigrator`, `UserDirectory`, `InviteDirectory`,
`ControlPlaneResiduals`, `src/Lola.ControlPlane/OpenIddict/`, the login lookup,
`docs/pii-data-map.json` and the data-plane/OpenBao runbooks. ADR-0002/0003/0004/0005.
Split this phase into design, implementation and rehearsal subtickets if needed.

1. Inventory identity columns, invite values, token payloads, registry metadata,
   indexes, audit exceptions, WAL, snapshots and backup copies. Classify each:
   retain opaque, encrypt, blind-index, hash-only, or remove. Bind organization
   and display name to the classification map. Password hashes and one-time
   token hashes remain hashes; they are not reversible search indexes.
2. **Approval gate:** document file-key bootstrap, field/index key domains,
   rotation, outage behavior, restore and erasure. Resolve the control-plane
   file key via an operator-provisioned wrapped secret and an independent
   workload identity, never via a row inside the locked DB. Use per-tenant
   transit protection for tenant identity fields. Specify tenant-aware login
   discovery before changing email lookup; do not replace it with a global
   plaintext or unsalted email index. Obtain approval for ADR changes first.
3. Implement keyed open for **every** consumer, including migrations, health
   probes, OpenIddict stores and maintenance tools. Apply the key before any
   read. Implement randomized field ciphertext plus keyed, normalized equality
   indexes for lookup/uniqueness. Ensure invite acceptance and login know their
   tenant before resolving index keys; keep unauthorized lookup errors generic.
4. Build an offline, resumable migration utility/runbook: stop writers,
   checkpoint, verify an independently recoverable protected backup, copy to
   a separate keyed destination, transform existing fields/indexes, and verify
   counts, FK/unique constraints, token behavior and audit chains. Switch only
   after validation. No plaintext fallback, plaintext temp export or migration
   in a web request. Define rollback **before reopening writes**; after new
   writes, require a reconciled recovery plan, not a stale-file swap.
5. Track legacy files, WAL and backups separately: new encryption does not
   retroactively protect old copies. Document restricted custody, lawful retention
   and approved disposal. Preserve independent audit history. Rehearse recovery
   of a valid tenant and non-recovery of erased tenant identity fields from a
   restored backup; an old key-service backup must not silently undo erasure.
6. **Prove on disposable fixtures only:** unkeyed/wrong-key opens fail; a keyed
   raw query sees ciphertext, not marker names or emails; normalized same-tenant
   lookup works and same email in two tenants is unambiguous. Scan produced DB,
   WAL, snapshot and logs for synthetic markers. Exercise interrupted migration,
   missing key service, restore, and erasure. Do not convert or delete a user's
   existing database as part of implementing this phase.

## Phase 8 - Make production fail closed at startup

**Start:** `src/Lola.Api/Program.cs`, `DevSigningKey`, `DevUserSeeder`,
`OidcClientSeeder`, client cookie options, `.env.example` and deployment runbooks.
ADR-0004/0005/0007/0009. Local compose remains usable without production secrets.

1. Build a validated production options profile. Require managed/protected OIDC
   signing and encryption keys, the canonical issuer and registered HTTPS
   callback origins, the control-plane key bootstrap, scoped OpenBao access,
   secure cookies and sufficient independent session secrets. Never auto-create
   dev keys or login identities in Production.
2. Reject stub or omitted production vault/scanner configuration. For pricing,
   fees, packages and other optional capabilities, require a real configured
   adapter **or explicitly disable the feature**; never substitute simulated
   financial results. Validate TLS/certificate verification for every enabled
   hop; where a component needs a terminating proxy, document both sides and
   require an approved protected transport. Do not trust arbitrary forwarded
   headers to satisfy OIDC HTTPS validation.
3. Separate liveness from readiness. Report not-ready when a required key,
   migration, scanner or backing service is unusable; keep messages free of
   credentials and borrower data. Document certificate/key rotation, least-
   privilege runtime vs migration credentials and recovery procedures.
4. **Prove:** table-driven Production host tests for omitted, stub, insecure
   and valid configurations; each unsafe case fails before serving protected
   traffic. Run the positive profile with disposable certificates and keys.
   Check headers/cookies at both BFFs, and keep Development and CI explicitly
   isolated. Do not weaken production validation to make a fixture pass.

## Phase 9 - Implement true server-side BFF custody

**Start:** both `clients/*/lib/auth.ts`, callback/logout handlers, BFF route
handlers and portal session-policy tests. Read each client `AGENTS.md` before
editing. ADR-0003/0005/0008/0011.

1. **Decision gate:** present the discrepancy (encrypted AT/RT in browser cookies
   versus server-only token custody). Recommended design: opaque random browser
   session ID; LOLA-owned server-side session store with encrypted tokens,
   client audience, subject, tenant, auth version, idle/absolute expiry and
   revocation state. Specify store owner, failure domain, key rotation and
   authenticated BFF-to-store access. Do not expose control DB files/keys to
   Next.js or introduce a new database/service without approved design/ADR.
2. Implement create/read/touch/refresh/revoke behind a server-only interface.
   Hash stored handle lookups, separate portal and officer namespaces, and use
   atomic compare-and-swap/locking for rotating refresh tokens across instances.
   Preserve each client's session limits; touch must not extend absolute expiry.
3. Replace chunked token cookies with one opaque, Secure, HttpOnly, host-scoped
   cookie per client. Preserve PKCE/state checks, safe local redirect validation
   and same-origin mutation protections. Logout/disable must invalidate server
   state, not just delete a browser cookie. Clear legacy chunks and require
   re-login at cutover; do not silently accept old token-bearing cookies.
4. Share only session mechanics across the two clients; keep each client's
   authorized paths, expiry policies and role-specific UI separate. Never return
   AT/RT from `/auth/me`, log them or include them in captured browser evidence.
5. **Prove:** synthetic end-to-end login/refresh/logout on both clients,
   multi-instance concurrent refresh, idle and absolute expiry, replay after
   logout, wrong-client handle and store outage. Inspect response cookies for
   opaque handles only and verify old-format cookies cannot authenticate.

## Phase 10 - Close against the original findings

1. Re-run the permission matrix on the merged code. Include roleless users,
   tenant service versus global automation, revoked partner links, plaintext
   exports, protected artifacts and both ingress adapters. Record each result
   against its finding and phase, not just a total green-test count.
2. Recheck all ten invariants in `AGENTS.md`. Use phase 7 for at-rest controls,
   erasure and SQLite pragmas; wire-payload allowlist tests for egress; phase 5
   and synthetic log/trace checks for audit and redaction; phase 8 for TLS and
   secret custody; existing submitted-snapshot immutability tests; and phases
   2/4 for masked reads. Do not treat a regex test as proof about every sink.
3. Rehearse startup, migrations, key rotation, loss of a dependency and restore
   on isolated synthetic data. Record recovery time, what remains recoverable,
   and what must stay erased. List operational assumptions that code cannot
   prove, named owners, and release-blocking exceptions. No live destructive
   action is authorized by this plan.
4. Update `docs/threat-model.md`, `docs/identity-runbook.md`, `docs/openbao-runbook.md`,
   `docs/data-plane-runbook.md`, the PII map, README and affected wiki pages.
   Update stale role/transport/custody claims to describe the verified design.
   Record the OWASP/architecture hardening gate, unresolved risks and approved
   ADRs on the parent. If Sonar findings were remediated, perform the required
   post-merge rescan. Do not close with a critical finding outstanding.

### Checks and proof for every implementation PR

- Run focused tests from the repo root, e.g. `dotnet test tests/Lola.Api.Tests --filter FullyQualifiedName~PlatformOpsTests` for phase 1. Subsequent phases use their cited fixture(s) plus new negative/failure cases.
- Run `dotnet format --verify-no-changes`, `dotnet build -c Release`, and the full `dotnet test` with the documented live-test environment. Run Release XPlat lcov collection and `node tools/covercheck.mjs <lcov.info>` on **only the current run's** reports; enforce the repository's 80% per-area floor. Do not skip live collections to obtain a green run.
- Run client `lint`, `typecheck`, `test` and `build` per repository gates. For browser/session changes run Playwright on both affected auth flows; for visible changes use the isolated wiki-screenshot process with synthetic data only.
- Post acceptance-to-evidence mapping, CI link and code-shape report per `AGENTS.md`. Include expected and observed deny/allow behavior and migration/rollback evidence where applicable. Keep proofs free of secrets. Pause after opening the PR; the next phase begins only after the prerequisite merges.
