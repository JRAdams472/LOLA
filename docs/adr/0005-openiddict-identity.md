# ADR 0005: openiddict: identity

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

Identity — OpenIddict

## Consequences

OpenIddict (Apache-2.0) is the OIDC/OAuth2 server: auth code + PKCE for officer console and borrower portal, client-credentials for service-to-service. Custom Dapper-backed IOpenIddict*Store implementations keep the stack EF-free. Roles: TenantAdmin, LoanOfficer, Borrower, Service; group→role→data-visibility map decides masked vs plaintext access.
# ADR Keeps all-C# stack and tenant-aware scoping. Keycloak rejected (non-C# service); ASP.NET Identity alone rejected (weak for future SSO/federation). Cost: custom stores.

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision



## Consequences


