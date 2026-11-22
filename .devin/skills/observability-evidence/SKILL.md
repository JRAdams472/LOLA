---
name: observability-evidence
description: Gather runtime evidence (Seq logs, Jaeger traces, SonarQube findings, docker logs) for LOLA proof-of-completion comments and SonarQube rescans. Use whenever a ticket needs run-output evidence, when attaching logs/traces to Linear, or when running a SonarQube rescan gate.
---

# Evidence gathering (observability)

Seq and Jaeger run in the compose stack and are both queryable over plain
HTTP — no MCP needed. Use them to attach runtime evidence (logs, traces)
to phase proof-of-completion.

- **Seq** (structured logs, `http://localhost:5441` — host port remapped
  for sibling coexistence; container still `:80`):
  ```bash
  # Recent events as JSON
  curl -s 'http://localhost:5441/api/events?count=50'
  # Filtered — e.g. errors only (Seq filter expression syntax)
  curl -s 'http://localhost:5441/api/events?count=50&filter=level%20%3D%20%27ERROR%27'
  ```
- **Jaeger** (traces, `http://localhost:17686` — the v2 API is under
  `/api/v3/`, NOT `/api/`):
  ```bash
  curl -s 'http://localhost:17686/api/v3/services'
  # start_time_min/max are REQUIRED and must be RFC3339
  curl -sG 'http://localhost:17686/api/v3/traces' \
    --data-urlencode 'query.service_name=lola' \
    --data-urlencode 'query.limit=10' \
    --data-urlencode 'query.start_time_min=2026-01-01T00:00:00Z' \
    --data-urlencode 'query.start_time_max=2030-01-01T00:00:00Z'
  ```
- **Raw fallback**: `docker logs lola-api-1 --since 30m` (JSON output).
- **SonarQube Community Build** (`http://localhost:9000`, project key
  `lola`): the `sonarqube` MCP server (`.devin/mcp_config.local.json`)
  exposes issues/hotspots/quality-gate/measures tools; the API is plain
  HTTP with token auth (`-u "$TOKEN:"`, token as username, empty
  password). Rescan by mounting **source subdirs only** — a whole-repo
  mount crawls `node_modules` and CI leftovers:
  ```bash
  docker run --rm \
    -v "$PWD/src:/usr/src/src" -v "$PWD/tests:/usr/src/tests" \
    -v "$PWD/migrations:/usr/src/migrations" \
    -e SONAR_TOKEN="$TOKEN" sonarsource/sonar-scanner-cli \
    -Dsonar.host.url=http://host.docker.internal:9000 -Dsonar.projectKey=lola \
    -Dsonar.sources=. -Dsonar.exclusions='**/bin/**,**/obj/**' \
    -Dsonar.cs.vscoveragexml.reportsPaths=coverage.xml
  ```
- **MCP helpers configured** (`.devin/mcp_config.local.json`, gitignored):
  `playwright` (real screenshots for UI proof), `linear` (ticket
  workflow), `github`, `sonarqube`.
- **Boy-scout rule for findings:** when a change touches a file with open
  low/info-severity SonarQube findings, fix them in the same change when
  trivial and low-risk. Skip nontrivial findings rather than ballooning
  the diff.
- **No PII in evidence:** Seq/Jaeger payloads and `docker logs` output
  must never contain plaintext PII — if they do, that's invariant-3
  breakage, file it as a bug, and redact before quoting in tickets.
