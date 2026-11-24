# M21 — TRID tolerance engine (MPE-296)

Reg Z §1026.19(e)(3) fee-variance tracking on top of the shipped
`TridClock` (six-field trigger + 3-day LE deadline). Every fee on an
LE/CD line classifies into a statutory bucket — **0%** (cannot
increase), **10%** (aggregate increases capped at 10%), or
**unlimited** — and each new disclosure version is diffed against the
baseline so violations surface as conditions + events, never silently.

Conforms to **ADR-0002** (Dapper + DbUp `0018_trid_tolerance.sql`),
**ADR-0003** (all rows in the tenant file), **ADR-0006** (classification
is deterministic over `fee_items`; the diff result lands in the
document/events surface — snapshots stay read-only).
Invariants: **4** (resets, violations, cures are hash-chained attributed
events), **6** (tolerance math reads append-only evals — never mutates
them; each disclosure version binds to an eval id), **8** (variance UI
inherits role visibility — no new reveal path).

## Design

### Buckets — `TridTolerance` (Lola.Domain, pure)

Classification inputs per `fee_items` line: `fee_code`, `section`,
`paid_to`, `selected_from_list`. Registry table `tolerance_classes`
maps `fee_code → (shoppable, bucket_hint)`; the domain rules:

- **0%** — section A/B (lender/affiliate + can't-shop services),
  transfer taxes, anything `paid_to` lender/affiliate
- **10%** — recording fees + section C services when
  `selected_from_list = 1`
- **unlimited** — everything else; shopped-off-list services
- **fail closed** — unknown `fee_code` → 0% (strictest)

`fee_items` gains `paid_to TEXT` + `selected_from_list INTEGER`
(migration, defaults NULL → conservative). `Diff(baselineItems,
compareItems)` → per-bucket variance: each 0% line delta > 0 →
violation; sum of 10% deltas > 10% of baseline bucket total →
violation; unlimited never violates.

### Disclosure versions — `disclosure_versions`

`(version_id, application_id, kind le|cd, eval_id, doc_id, seq,
reset_of_version_id, created_utc)` — binds each generated
le-package/cd-package document row to the `fee_evals` snapshot it
rendered. `cd-package` joins `documents.kind` (additive to the
existing `le-package|adverse-action` CHECK — same rebuild pattern as
prior constraint changes). `reset_of_version_id` links a re-issue to
the baseline it rebases.

### Changed circumstances — `tolerance_resets`

`(reset_id, application_id, reason_code, affected_fee_codes_json,
rebased_version_id, actor, created_utc)` + `LoanEventKind.ToleranceReset`.
Valid reason codes: `changed_info | borrower_request | rate_lock |
extraordinary | eligibility | delayed_settlement`. Out-of-set or
empty `affected_fee_codes` → refused + audited. A reset makes the
next disclosure version a new baseline for the listed fees only —
prior fees keep the original baseline.

### Violations + cure

`LoanEventKind.ToleranceViolation` + `ToleranceCure`;
`ConditionKind.ToleranceCure` (blocking — gates submit like other
blocking conditions). `EvaluateTolerance` runs on each completed
disclosure version; violations open one condition per application
(deduped while open). `RecordCure` closes the condition with reason +
writes the cure event.

## Phases

| # | Branch | Scope |
|---|---|---|
| 1 | `MPE-<p1>` | Migration `0018` (`tolerance_classes` seed, `fee_items` cols, `disclosure_versions`, `tolerance_resets`, `documents` CHECK rebuild for `cd-package`) + `TridTolerance` domain (classify + diff + violations) + unit tests |
| 2 | `MPE-<p2>` | `EvaluateTolerance`/`RecordCure`/`RecordChangedCircumstance` handlers, Dapper ports, version binding on doc generation, event/condition wiring, API endpoints |
| 3 | `MPE-<p3>` | Officer workspace variance surface (per-bucket badge/line), e2e, jest |

## Non-goals

- CD document template changes (cd-package rides `IDocumentService`
  with template-provided fields — ISPPr owns layout)
- Rate-lock fee interplay beyond the `rate_lock` reset reason
- Refund disbursement mechanics — cure is tracked as a recorded act,
  money movement is out of scope

## Hardening gate

Milestone closes with the OWASP + ADR-drift gate comment on MPE-296
per `AGENTS.md`.
