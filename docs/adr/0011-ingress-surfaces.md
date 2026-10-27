# ADR 0011: Ingress surfaces — REST + in-process GraphQL

**Status:** Accepted (locked — MPE-63 megaplan, user decision 2026-10-27)

## Decision

Two ingress adapters over the same Wolverine-mediated application layer:
**REST** for system/service endpoints (health, auth, webhooks, file
upload/download) and **GraphQL** at `/graphql` via **HotChocolate.AspNetCore
(MIT, core package only — no ChilliCream Pro/Fusion licensed modules)**
for application data consumed by the UIs. Resolvers call the same mediator
handlers as REST — no business logic in the ingress layer.

Day-one hardening: max execution depth 8, enforced cost complexity limit
300, exception details dev-only. PII masking (ADR-0008) must hold on
GraphQL fields exactly as REST — resolvers pass through the same
field-class masking pipeline.

## Consequences

Single-process, all-C# (no separate Node BFF — the lena2 pattern was
rejected for this codebase). GraphQL surface joins the per-milestone
OWASP gate scope. File upload/download stays REST/presigned-URL —
GraphQL never carries binaries.
