# ADR 0005: OpenIddict identity

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

Identity — OpenIddict

## Consequences

OpenIddict (Apache-2.0) is the OIDC/OAuth2 server: auth code + PKCE for officer console and borrower portal, client-credentials for service-to-service. Custom Dapper-backed IOpenIddict*Store implementations keep the stack EF-free. Roles: TenantAdmin, LoanOfficer, Borrower, Service, plus the M19 TPO partner roles Broker and Realtor — external actors inside the lender's tenant boundary, link-scoped to applications and never granted `role_visibility` rows; group→role→data-visibility map decides masked vs plaintext access.
Keeps all-C# stack and tenant-aware scoping. Keycloak rejected (non-C# service); ASP.NET Identity alone rejected (weak for future SSO/federation). Cost: custom stores.

Control-plane extension (MPE-276 / Phase 7, design `docs/control-plane-encryption-design.md`): login lookup is tenant-scoped, not global. The caller presents a tenant hint (org/tenant slug); the slug resolves to a tenant id via its keyed blind index, then email resolves inside that tenant via the keyed normalized `(tenant_id, email)` index. The global plaintext email lookup is removed — the same email in two tenants stays unambiguous, and an unknown or missing tenant hint fails with the same generic error as a bad password. Invite acceptance and admin flows already carry tenant context. Human grants carry `lola_auth_ver` (Phase 6) so stale authority is revalidated against the user directory at every issuance boundary.
