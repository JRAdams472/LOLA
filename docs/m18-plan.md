# M18 — Local document intelligence + auto-conditions (MPE-246)

Phase-2 milestone 4 of 6. Depends on M15 (conditions + `loan_events`).
Deterministic classification inside the tenant boundary — **no AI
model, no rules engine, no external service**. A hand-rolled
declarative rule table behind the `IDocClassifier` seam: the seam is
the hedge that lets a real model (or Zen-style engine) swap in later
without touching the pipeline — the ADR-0007 pattern applied to
document intelligence.

Conforms to **ADR-0006** (doc classification rides the existing
document-store pipeline, not a parallel store), **ADR-0007** (adapter
seam — `DOC_CLASSIFIER` env selects impl, second impl proves the
swap), **ADR-0003/0004** (tenant-side rows, vault-encrypted results),
**ADR-0011** (no new ingress — classification happens in the existing
vault drain). Invariants: **1** (classification result `vault:`-
encrypted at rest; plaintext head exists only in-memory), **2** (doc
bytes never leave LOLA — classifier is in-process), **3** (audit/events
carry kind+findings metadata, never extracted values or content), **4**
(every classification + condition transition on both spines).

## Phases

| # | Ticket | Delivers |
|---|---|---|
| 1 | MPE-261 | `IDocClassifier` seam + `DocClassification` contract + **rule classifier** (first impl — declarative rule table: filename/mime/text markers → kind; field extractors; validity windows → findings) + **stub classifier** (echoes declared kind — proves env swap) + `DOC_CLASSIFIER` env + unit tests |
| 2 | MPE-262 | Pipeline insertion + persistence — `0016` migration adds `classification_json` (vault:-encrypted) + `classified_utc` to `vault_docs`; scan processor tees a bounded plaintext head to the classifier after a Clean verdict **before** promote; `RecordDocClassification` handler stores the encrypted result + `doc_classified` event/audit (metadata only); classifier failure → audited, promotion still proceeds (AV is the gate — intelligence is additive) |
| 3 | MPE-263 | Auto-conditions — `doc_required` conditions linked `doc_kind` auto-close on kind match ("satisfied by vlt-…"); findings (`expired_doc`/`kind_mismatch`/`unreadable`) open blocking replace-doc conditions linked `vault_doc`; `distance_check` heuristic (extracted employer address vs. subject-property address — coarse state/locality match, documented) opens a `distance_check` condition; task-eval convergence; events + audit on every transition; actor `auto:classifier` |
| 4 | MPE-264 | Surfaces + close-out — officer vault row shows classification (kind, confidence, finding count, *which* fields were extracted — never values: masked-by-default); borrower portal sees the condition list + what each needs; live marquee (synthetic W2 → clean → `w2` + extracted fields → `doc_required` auto-closed; expired doc → condition; env-swap stub proof); README/wiki; hardening gate; close MPE-246 |

## Design calls

- **Rule-driven, not AI.** The megaplan line says "rule classifier" —
  the shipped impl is a declarative rule table (one record per doc
  kind: filename regexes, content markers with weights, field
  extractors, validity windows) evaluated by plain C#. No Zen Engine —
  a dozen rules doesn't need a decision-table runtime, and the engine
  wouldn't solve field extraction anyway. `IDocClassifier` keeps the
  swap reversible.
- **Text signal is strings-style extraction.** The classifier pulls
  readable ASCII/UTF-8 runs from the captured head — works on `.txt`
  and text-bearing PDFs; image-only docs fall back to
  filename/mime/declared-kind at lower confidence (and can produce an
  `unreadable` finding, which is honest behavior, not a gap). No PDF
  lib, no OCR in MVP — both are later swaps behind the same seam.
- **Bounded head, not buffered doc.** The scan drain already streams
  plaintext decrypt→INSTREAM (never durable). Classification gets a
  capturing tee of the first ~2 MiB — enough for markers/extractors
  (doc headers carry them), bounded memory under the 25-row batch,
  stream discipline preserved.
- **Classify before `clean`, but AV is still the gate.** Order in the
  drain: Clean verdict → classify → persist → promote. A classifier
  error is audited and the doc still promotes — intelligence is
  additive to the pipeline, never a second gate.
- **Kinds map to the declared-classification vocabulary** —
  `w2|paystub|bank_statement|id|flood_cert|misc`. `kind_mismatch`
  compares detected vs. declared, which is what makes
  borrower-visible "you uploaded the wrong doc" conditions possible.
- **Extracted fields are PII-class.** The whole `DocClassification`
  (fields + findings + confidence) lands `vault:`-encrypted on the
  doc row; audit/events see `{kind, confidence, finding names, field
  names}` — never values. Officers see the same masked metadata on
  the vault row; field values are behind encryption, reveal-eligible
  later if a ticket wants it.
- **Auto-conditions converge with M15/M16.** New conditions open via
  the standard handler path (same-transaction events + audit);
  closing one resolves its queue task in the same `TaskEvaluator`
  pass — identical to M17's feedback conditions.
- **`distance_check` is intentionally coarse.** Employer locality vs.
  property locality (parsed from extracted text vs. the application
  doc) → a review condition, not a geocode call. Documented as
  heuristic-grade; a geo service is an ADR-0007 vendor decision later.

## Acceptance map (MPE-246 bullets → proof)

- *W2 classified `w2` with extracted fields; doc-required condition
  auto-closes* → P1 unit tests + P4 live marquee
- *Expired/unreadable doc opens a condition visible to borrower +
  officer* → P3 handler tests + P4 marquee + portal surface
- *Classification before `clean`, findings on the record, encrypted at
  rest* → P2 migration/handler tests + marquee reads the row
- *Second classifier impl swaps via env without touching pipeline
  code* → P1 `stub` impl + env-swap test + marquee flag flip
