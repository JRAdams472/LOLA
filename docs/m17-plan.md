# M17 — Downstream validation feedback loop (MPE-245)

Phase-2 milestone 3 of 6. Depends on M15 (conditions + `loan_events`)
and M16 (task queue). MPEx and ISPPr push structured validation results
back into LOLA; findings drive condition transitions, exception tasks,
events, and audit — closing the loop the megaplan named.

Conforms to **ADR-0007** (vendor seams — ingress reuses the M13 webhook
pattern, no new channel) and **ADR-0011** (REST push; Wolverine
handlers). Invariants 2 (payload-minimal contract + reflection
firewall), 5 (HMAC-signed ingress), 4 (audited transitions).

## Phases

| # | Ticket | Delivers |
|---|---|---|
| 1 | MPE-257 | `ValidationFeedback` contract (source, app ref, finding kind — `data_error`/`missing_field`/`exception`/`cleared` — severity, condition/entity links, `feedback_id`) + `vendor_feedback` dedupe ledger migration (tenant-side; the rows it protects are tenant-side) + PII firewall extension to the contract |
| 2 | MPE-258 | `RecordValidationFeedback` handler — finding→condition mapping (auto-close matching open condition, open new condition, emit exception `TaskKind` onto the queue); `condition_*` + `validation_*` `loan_events` + audit on every transition |
| 3 | MPE-259 | `POST /api/vendor-webhooks/{source}/validation` — bounded 64KiB body, tenant lookup, `X-Vendor-Signature` HMAC over raw body via `IVendorCredentialSource`, `feedback_id` dedupe (replay → 200 no-op), `vendor-webhook` rate policy; forged/unsigned → 401 |
| 4 | MPE-260 | Sibling adapter contract doc (`docs/vendor-feedback-contract.md`) + live marquee (signed MPEx clear → auto-close + event/audit; ISPPr exception → condition + queue task; forged/replay rejection) + hardening gate + close-out |

## Design calls

- **Dedupe is tenant-side.** The M13 order-level dedupe lives with the
  order; feedback has no order row, so `vendor_feedback` (feedback_id
  PK, source, received_utc, finding_id → entity link) lands in the
  tenant file next to the conditions it guards. Control plane stays
  untouched.
- **Exception findings emit tasks, not conditions alone.** An M16
  `validation_exception` task (High, skill from source kind) lands on
  the queue so officers see exceptions in the same ranked list —
  the whole point of the M15→M17 chain.
- **No new auth surface.** `/{source}/validation` reuses
  `VendorWebhookAuth` + `IVendorCredentialSource` — a sibling needs a
  per-source webhook secret, same as order callbacks.
