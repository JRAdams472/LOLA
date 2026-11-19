# LOLA Phase-2 Megaplan — MPE-83

Strategic alignment against enterprise LOS patterns (Vesta queue,
Empower doc intelligence, Lodasoft TPO). Builds entirely on shipped
seams — **no architecture drift**: Dapper/DbUp, SQLite-file-per-tenant,
OpenBao, OpenIddict, ADR-0007 adapter seams, masked-by-default,
hash-chained audit.

MVP data flow stays intact; Phase 2 adds the *operating layer* on top:
`state → conditions → tasks → queue` plus external TPO audiences.

## Locked design calls (from planning Q&A)

1. **Mutation event log, not literal event sourcing** — append-only
   field-change events with hashed PII alongside existing snapshot
   documents. Audit-grade trail; no state-rebuild, no ADR-0006
   conflict.
2. **Doc intelligence ships as seam + rule classifier** —
   `IDocClassifier` with deterministic rules now; self-hosted ML model
   is a later swap-in behind the seam (M13 stub pattern).
3. **TPO = scoped external roles inside the lender tenant** — row-level
   loan visibility; same SQLCipher file (ADR-0003 unchanged). No
   cross-file federation.
4. **Six milestones** M15–M20, mirroring the M14 close-out tail.

## Milestones

| # | Deliverable | Ticket | Deps |
|---|---|---|---|
| M15 | Mutation event log + conditions domain — `loan_events` append-only hash-chained (field changes, overrides, escalations, hashed PII); `conditions` entity (auto/manual, open/closed/waived, blocking); written on existing mutation paths | MPE-243 | M14 |
| M16 | Task engine + Next-Best-Task queue — control-plane PII-free task/queue schema (kind, priority, skill tags, due); tenant-side evaluator deriving tasks from conditions/step events; officer unified ranked queue | MPE-244 | M15 |
| M17 | Downstream feedback loop — structured validation webhooks MPEx/ISPPr → LOLA (extend M13 ingress: signature, dedupe, rate policy); auto-close conditions, log exceptions as tasks | MPE-245 | M15, M16 |
| M18 | Local document intelligence — `IDocClassifier` seam + rule classifier; pipeline quarantine→ClamAV→classify→clean; classification → extracted data + auto-conditions; portal condition display | MPE-246 | M15 |
| M19 | TPO sub-tenancy — broker/realtor external roles + row-scoped loan visibility; TPO portal surface; dynamic pre-approval letters via ISPPr minimal-payload requests | MPE-247 | M15 |
| M20 | Phase-2 hardening, UAT & close-out — threat-model delta, drill re-runs, UAT matrix extension, wiki refresh, gate, close-out tickets | MPE-248 | M15–M19 |

## Per-milestone ADR/invariant mapping

- **M15** — ADR-0002/0003/0006; invariants 4 (hash-chained append-only),
  1 (hashed PII in events), 6 (snapshots stay immutable — events are
  additive)
- **M16** — ADR-0005 (control-plane placement); invariants 2/3
  (queue carries PII-free metadata only — app refs, never values)
- **M17** — ADR-0007/0011; invariants 2/5 (signed ingress, minimal
  payloads); webhook reuse of M13 signature/dedupe/rate-limit proven
  patterns
- **M18** — ADR-0006/0007; invariants 1/2 (classification stays inside
  the tenant boundary — no doc bytes leave LOLA; results are
  PII-class ciphertext)
- **M19** — ADR-0003/0005; invariants 8 (masked default for external
  roles), 5 (TLS + OIDC), 4 (audited external access)
- **M20** — all ADRs, all invariants; program-level gate

## Non-goals

- True event-sourced state rebuild (mutation log only)
- Production ML classification models (seam + rules now)
- Cross-tenant loan federation (TPO scoped in-lender only)
- e-signature (ISPPr-side, MPE-239)

## Hardening gate

M20 closes with the program-level gate (OWASP + app hardening + ADR
drift) per `AGENTS.md`; every milestone carries its own gate comment
before close.
