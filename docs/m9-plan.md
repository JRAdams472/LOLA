# M9 (MPE-72) plan — Document adapter & ISPPr integration

> **Retroactive reconstruction (MPE-236).** The M9 plan was attached to
> the Linear milestone ticket but never committed alongside the other
> `mN-plan.md` docs. This file reconstructs the shipped plan from the
> MPE-189–192 phase tickets and merged code so the docs index is
> complete. It documents what was built, not a proposal.

The `IDocumentService` seam — disclosure packages through a pluggable
adapter (ISPPr first), with per-tenant vendor credentials, a
manifest-bounded egress contract, and a presigned-URL retrieval
registry. Mirrors the M7/M8 adapter architecture.

## Vendor contract (ISPPr, surveyed)

- Per-tenant ISPPr client provisioned during M3 tenant provisioning
  (`isppr-docs` step): `client_id` + API key, revoked on erasure
  (`isppr_key_revoked` control-plane audit event).
- Document request = one master payload: 1003 data + locked pricing +
  aggregated fee list → package (LE + initial disclosures, 1003
  summary, adverse action).
- Results are zip packages served via presigned URLs, TTL ≤ 15 min.

## Design calls

1. **Manifest-bounded egress** — not PII-free like MPEx; disclosure
   packages legitimately need name/address/loan terms. The contract is
   the *manifest*: only template-required fields may cross the wire —
   the same philosophy as the M13 per-kind vendor allowlist.
2. **Outbox consistent with M7/M8** — `doc_outbox` same-tx enqueue,
   dedupe on request hash, worker drain.
3. **Registry over mailbox** — a `documents` table records each
   generated package (id, kind, filename, timestamps, status);
   download goes through the registry → short-TTL presigned URL, never
   a long-lived link or proxied bytes.
4. **Erasure revokes vendor access** — crypto-shred calls the ISPPr
   admin seam to revoke the tenant's client/key before files are
   shredded (control-plane residuals folded into M6's lifecycle).

## Phases (as executed)

- **P1 — `IDocumentService` seam + manifest payloads** (MPE-189,
  ADR-0007) — port, normalized request/result DTOs, manifest-driven
  payload builder (`DisclosurePayloadBuilder`), stub adapter, egress
  firewall extension for doc fields.
- **P2 — ISPPr adapter, per-tenant provisioning, doc registry +
  outbox** (MPE-190) — live adapter + session client, `isppr-docs`
  provisioning step, `documents` schema, `DocOutboxWorker`,
  `POST …/{appId}/packages` → 202.
- **P3 — registry reads, adverse-action e2e, republish, erasure
  revoke** (MPE-191) — `GET …/documents`, `GET …/download` → 302
  presigned ≤15 min, template republish endpoint,
  `isppr_key_revoked` on erase.
- **P4 — doc marquee e2e + hardening gate + close-out** (MPE-192) —
  full generate→download e2e, adverse-action leg, M9 gate findings.

## Invariants / ADRs

ADR-0002 (Dapper/DbUp), ADR-0006 (hybrid doc store), ADR-0007 (adapter
seam), ADR-0011 (ingress surfaces); invariants 2 (egress minimization),
5 (TLS + svc auth), 7 (no secrets in repo — creds via provisioning +
OpenBao), 9 (erasure revokes downstream access).
