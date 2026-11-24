# ADR 0005: OpenIddict identity

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

Identity — OpenIddict

## Consequences

OpenIddict (Apache-2.0) is the OIDC/OAuth2 server: auth code + PKCE for officer console and borrower portal, client-credentials for service-to-service. Custom Dapper-backed IOpenIddict*Store implementations keep the stack EF-free. Roles: TenantAdmin, LoanOfficer, Borrower, Service, plus the M19 TPO partner roles Broker and Realtor — external actors inside the lender's tenant boundary, link-scoped to applications and never granted `role_visibility` rows; group→role→data-visibility map decides masked vs plaintext access.
Keeps all-C# stack and tenant-aware scoping. Keycloak rejected (non-C# service); ASP.NET Identity alone rejected (weak for future SSO/federation). Cost: custom stores.

Control-plane extension (MPE-276 / Phase 7, design `docs/control-plane-encryption-design.md`): login lookup is tenant-scoped, not global. The caller presents a tenant hint (org/tenant slug); the slug resolves to a tenant id via its keyed blind index, then email resolves inside that tenant via the keyed normalized `(tenant_id, email)` index. The global plaintext email lookup is removed — the same email in two tenants stays unambiguous, and an unknown or missing tenant hint fails with the same generic error as a bad password. Invite acceptance and admin flows already carry tenant context. Human grants carry `lola_auth_ver` (Phase 6) so stale authority is revalidated against the user directory at every issuance boundary.

BFF token custody (MPE-289 / remediation phase 9, design approved on MPE-278): the browser holds an opaque random handle only — never token-bearing cookies. Access and refresh tokens live in the control-plane `bff_sessions` table as `control-plane`-domain transit ciphertext, mediated solely by the API's `/bff-sessions` surface: dedicated confidential clients (`lola-bff-console` / `lola-bff-portal`) authenticate via client-credentials and receive a minted `lola_bff_ns` claim that pins the session namespace (wrong-client handles fail by construction). The API owns refresh rotation end-to-end — per-handle single-flight plus a `rev` compare-and-swap serializes concurrent resolves across BFF replicas, and `invalid_grant` revokes the session rather than retrying on faith. Rows carry subject/tenant/`auth_version`, idle and absolute bounds (touches slide idle only, never absolute) and revocation state; resolve revalidates `auth_version` against the live user directory on every call, so phase-6 authority bumps deny a held session immediately. Logout revokes both the row and the upstream refresh grant. Session-store failure is fail-closed (deny), never a token fallback; handle lookups are SHA-256 hashes, and token-key rotation rides OpenBao transit rotation with no data rewrite. Next.js receives no control-plane files, keys, or mounts.
