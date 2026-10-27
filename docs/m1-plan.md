# M1 Plan — Solution Scaffold & Architecture Baseline (MPE-65)

Two phases → two subtickets → one PR each → milestone hardening gate.

## Layout (Clean Architecture)

```
src/Lola.Domain        entities, [PiiClass], errors — zero deps
src/Lola.Application   CQRS messages+handlers, pipeline behaviors, ports
src/Lola.Infrastructure Dapper/SQLite, ext impls — depends on Application
src/Lola.ControlPlane  control-plane persistence/identity — depends on Application
src/Lola.Api           composition root: REST + /graphql — depends on all
tests/Lola.Domain.Tests, Lola.Api.Tests  xUnit + Shouldly
migrations/            DbUp append-only NNNN_name.sql
```
Rule: Domain ← Application ← {Infrastructure, ControlPlane} ← Api (test-enforced).

## Packages (free/OSS only)

Wolverine.Fx (MIT — MediatR excluded: commercial), HotChocolate.AspNetCore
(MIT core only), Dapper, Microsoft.Data.Sqlite + SQLitePCLRaw.bundle_e_sqlcipher2,
DbUp+dbup-sqlite, OTel instr/exporter, xunit+Shouldly+coverlet.
FluentAssertions excluded (paid). Built-in OpenAPI + RateLimiting.

## Ingress (ADR-0011)

REST for system/auth/files; GraphQL `/graphql` (HotChocolate in-process)
for app data — resolvers over the same mediator. Day-one hardening:
depth/complexity limits, introspection policy, masking-compatible field
middleware.

## P1 — scaffold + API shell

sln + 5 src projects + 2 test projects; Wolverine + PingQuery/PingHandler
+ one pipeline behavior; /healthz, /api/health, /openapi/v1.json,
/graphql (ping query); problem-details error model; security headers;
CORS allowlist env; rate limiter; Dockerfile (non-root) + compose api :8200;
architecture-dependency test. **Acceptance:** compose up → :8200 healthy,
PR green.

## P2 — data layer + observability + CI

Microsoft.Data.Sqlite + sqlcipher bundle + Dapper wiring; control-plane
data/control.db; pragmas on every open (secure_delete, auto_vacuum=FULL,
WAL, foreign_keys); DbUp boot runner + 0001_control_plane.sql; OTel
traces→Jaeger + logs→Seq (exporter-free when unset); PII log-redaction
filter + test; /api/health DB check; CI dotnet job activates (format/
build/test/lcov/covercheck floor). **Acceptance:** healthy api w/ DB
check, spans in Jaeger, logs in Seq, CI green.

## Gate

OWASP + app + ADR review on MPE-65 after P2 (incl. GraphQL surface).
