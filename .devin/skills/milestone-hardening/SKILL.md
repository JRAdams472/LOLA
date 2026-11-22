---
name: milestone-hardening
description: Run the LOLA milestone hardening gate — OWASP review, application hardening, ADR-drift check — before a milestone ticket can be marked Done. Use when a milestone's last phase merges or when asked to harden/review the milestone's attack surface.
---

# Milestone hardening gate (required before milestone close)

Every milestone ends with a deep-dive hardening pass — not just the final
close-out milestone. The gate is smaller and cheaper per milestone than a
single end-of-program review, and findings surface while the attack
surface is still small:

1. **OWASP review** of the milestone's attack surface (Top 10):
   injection, broken access control, cryptographic failures, auth/session
   weaknesses, security misconfiguration, vulnerable/outdated
   dependencies, logging failures (**esp. any PII — invariant 3**),
   SSRF on outbound adapters, upload/file handling.
2. **Application hardening:** error-model consistency, rate limits,
   timeouts, input-validation coverage, secrets handling, new-dependency
   review (free/OSS and published ≥7 days).
3. **Architecture overview:** code vs. `docs/adr/*` — any drift is
   flagged and fixed, or escalated as an ADR amendment with user
   approval; never merged silently. `node tools/adrcheck.mjs` must stay
   green.
4. Findings post as a **comment on the milestone ticket**. Critical
   findings are fixed before close; the rest become Linear tickets.
   **A milestone is not Done until its hardening-gate comment exists.**
