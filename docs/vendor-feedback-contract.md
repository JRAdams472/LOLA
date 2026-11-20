# Vendor Validation Feedback Contract

Audience: MPEx and ISPPr teams implementing validation-feedback
callbacks into LOLA (M17, ADR-0011). This is the *inbound* channel —
downstream validators push structured findings; LOLA turns them into
conditions, queue tasks, and audit records.

## Endpoint

```
POST /api/vendor-webhooks/{source}/validation
```

`{source}` is your assigned source token:

| Source | System |
|---|---|
| `mpex` | MPEx pricing/eligibility validator |
| `isppr` | ISPPr document-package validator |

Any other source token returns **404** before the request body is
read — the source is the trust boundary.

## Authentication

Every request carries an HMAC signature over the **exact raw request
bytes** (no re-serialization, no canonicalization):

```
X-Vendor-Signature: sha256=<lowercase hex HMAC-SHA256>
```

Key: the tenant's `webhook_secret` stored at OpenBao KV path
`vendors/{tenant_id}/{source}`. Secrets are per tenant **and** per
source — MPEx and ISPPr hold different keys for the same tenant, and a
given source's key differs across tenants. Vendors hold no OIDC
tokens; this header is the only auth channel on the route.

The comparison is constant-time. A missing, malformed, or forged
signature returns **401**.

## Request body

JSON object, UTF-8, **at most 64 KiB** (enforced on the stream —
chunked requests without `Content-Length` are bounded too; over-limit
→ **413**).

| Field | Required | Meaning |
|---|---|---|
| `tenant` | ✔ | LOLA tenant id the application belongs to |
| `application_id` | ✔ | LOLA application reference the finding is about |
| `feedback_id` | ✔ | **Your** unique id for this receipt — the dedupe key |
| `finding` | ✔ | One of `data_error`, `missing_field`, `exception`, `cleared` |
| `severity` | ✔ | One of `info`, `warning`, `error` |
| `condition_id` | | For `cleared`: the open condition this resolves |
| `linked_entity` | | Entity kind a new condition binds to |
| `linked_entity_id` | | Entity id a new condition binds to |

The contract is **payload-minimal by design**: send references and
enum tokens, never field values or applicant data. You already hold
the data you are reporting on — nothing PII-class may ride this
envelope (the PII firewall verifies this member-for-member in CI).

## Finding semantics

| `finding` | LOLA behavior |
|---|---|
| `cleared` | Auto-closes the `condition_id`'s open condition on the application, with reason, `condition_closed` event, and audit record. If no matching open condition exists, the receipt is still recorded (`"action": "recorded"`) — it is not an error. |
| `data_error` | Opens a `data_validation` condition linked to your `feedback_id`. |
| `missing_field` | Opens a `data_validation` condition linked to your `feedback_id`. |
| `exception` | Opens a **blocking** `data_validation` condition **and** lands a high-priority `validation_exception` task on the officer queue (skill: `underwriting` for mpex, `processing` for isppr). |

Severity sharpens blocking: `exception` **or** `severity: "error"`
makes the condition blocking; `info`/`warning` on non-exception
findings stays advisory (non-blocking, no queue task) so advisory
traffic cannot flood the queue.

The task resolves automatically when its condition closes — including
via a later `cleared` finding. Corrections converge; nothing needs to
be "un-sent".

## Responses

| Code | When | Body |
|---|---|---|
| 200 | Receipt recorded | `{"status":"recorded","action":"condition_closed"\|"condition_opened"\|"recorded"}` |
| 200 | Replay — `feedback_id` already seen for your source | `{"status":"duplicate"}` |
| 400 | Malformed JSON, missing required field, or unknown `finding`/`severity` | problem details |
| 401 | Missing/forged signature, or tenant unknown/inactive | — |
| 404 | Unknown `{source}` in the path | — |
| 413 | Body over 64 KiB | — |
| 429 | Per-IP+path `vendor-webhook` rate policy exceeded | — |

## Dedupe semantics

Receipts dedupe on `(source, feedback_id)` in the tenant-side
`vendor_feedback` ledger — `INSERT OR IGNORE` semantics, so the ledger
write is the single decider even under concurrent delivery.

- Choose `feedback_id` values unique within your source (UUID or
  monotonic source key both work). MPEx and ISPPr may reuse the same
  id space — the composite key never collides cross-source.
- A replay answers `200 {"status":"duplicate"}` and performs **no**
  second domain write — safe to retry on timeout/5xx.
- A `cleared` finding and an `exception` finding are different
  receipts; send a distinct `feedback_id` per distinct finding.

## Audit trail

Every accepted receipt lands on both append-only spines:

- `validation_received` — tenant audit row + `loan_events` entry on
  the application's hash-chained mutation spine (field path
  `validation.{finding}`, value = severity only — never payload data).
- `condition_opened` / `condition_closed` — audit + event rows when a
  condition transitions.

The `condition_id` returned implicitly via `"action"` pairs with your
`feedback_id` — the opened condition links `linked_entity:
"vendor_feedback"`, `linked_entity_id: <your feedback_id>`, so the
ledger is joinable back to the receipt that raised it.

## Example

```bash
BODY='{"tenant":"t_acme","application_id":"app_01HX…","feedback_id":"mpex-vf-2026-0042","finding":"exception","severity":"error","linked_entity":"application","linked_entity_id":"app_01HX…"}'
SIG="sha256=$(printf '%s' "$BODY" | openssl dgst -sha256 -hmac "$WEBHOOK_SECRET" -hex | cut -d' ' -f2)"

curl -X POST "https://lola/api/vendor-webhooks/mpex/validation" \
  -H "Content-Type: application/json" \
  -H "X-Vendor-Signature: $SIG" \
  --data-raw "$BODY"
# → 200 {"status":"recorded","action":"condition_opened"}
```
