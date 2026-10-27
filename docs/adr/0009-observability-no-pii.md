# ADR 0009: observability: no: pii

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision

Observability — OTel, zero PII

## Consequences

OpenTelemetry → Jaeger (traces, host :17686) + Seq (structured logs, :5441). Exporter-free when env vars unset. Zero PII in logs/traces is invariant: a redaction filter + CI test enforce it; audit events record field classes only. Ports are remapped so LOLA runs alongside sibling stacks.
# ADR Same convention as MPEx/ISPPr. Any PII reaching logs is a severity bug (invariant 3), not a cosmetic issue.

**Status:** Accepted (locked — MPE-63 megaplan)

## Decision



## Consequences


