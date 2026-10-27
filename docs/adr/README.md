# Architecture Decision Records — LOLA

Locked decisions for the MPE-63 megaplan. **Milestone plans must conform to
these records.** Any proposed change requires an amended ADR plus explicit
user approval *before* code — never drift silently.

| ADR | Decision |
|---|---|
| [0001](0001-language-runtime.md) | C#/.NET 10 base language; free/OSS modules only |
| [0002](0002-dapper-dbup-no-ef.md) | Dapper + DbUp migrations; **no EF Core** |
| [0003](0003-sqlite-per-tenant.md) | SQLite file-per-tenant data plane; SQLite control plane |
| [0004](0004-openbao-envelope-encryption.md) | OpenBao transit envelope encryption + blind indexes |
| [0005](0005-openiddict-identity.md) | OpenIddict OIDC; custom Dapper stores |
| [0006](0006-document-store-hybrid.md) | MISMO documents in JSON1 columns + relational index (doc DB rejected) |
| [0007](0007-adapter-seams.md) | `IPricingService`/`IFeeService`/`IDocumentService`/`IVendorAdapter` seams |
| [0008](0008-masked-by-default.md) | Masked-by-default + role→visibility masking layer |
| [0009](0009-observability-no-pii.md) | OTel→Seq/Jaeger; zero PII in logs/traces |
| [0010](0010-workflow-coverage.md) | PR-only + pause-after-PR; coverage 80%/module, 85% goal |
| [0011](0011-ingress-surfaces.md) | REST + in-process HotChocolate GraphQL over one mediator |
