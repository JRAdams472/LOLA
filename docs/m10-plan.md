# M10 (MPE-73) plan — Borrower document vault

Secure storage + scanning for borrower-uploaded documents (paystubs,
W-2s, bank statements, ID): per-tenant MinIO bucket, ClamAV
quarantine→clean pipeline, LOLA-signed ≤15-min download URLs, and a
classification registry tied to the loan. Mirrors the M9 document
architecture wherever the contract allows.

## Vendor contract (MinIO + ClamAV, surveyed)

- **MinIO** — S3-compatible object store, dev stand-in is
  `ghcr.io/coollabsio/minio` (host `:9200`, already in compose).
  Plain S3 ops only — Put/Get/Copy/Delete/ListBuckets — via
  `AWSSDK.S3` (Apache-2.0). **No presign**: objects at rest are
  app-layer ciphertext, so a MinIO presigned URL would serve bytes
  no caller can use; the ≤15-min URL contract is carried by a
  LOLA-signed route instead.
- **ClamAV** — `clamav/clamav:stable` (host `:3310`, already in
  compose). Wire protocol is `zINSTREAM`: 4-byte length-prefixed
  chunks terminated by a zero-length chunk, reply `stream: OK` or
  `stream: <sig> FOUND`. Implemented over raw `TcpClient` — no
  package dependency.

## Design calls (locked 2026-11-07)

1. **Encryption: app-layer envelope, not SSE-KMS.** Each object
   gets a random 32-byte DEK, AES-256-GCM chunked AEAD streamed
   (memory bounded to chunk size, never file size); the DEK is
   wrapped by the tenant's transit key and stored as `wrapped_dek`
   on the registry row. MinIO only ever holds ciphertext —
   plaintext exists only in in-memory streams, so "no unencrypted
   temp files" and invariant 1 hold end-to-end. Tenant-key
   destruction crypto-shreds every vault object, bucket or no
   bucket. KES/KMS infra rejected: heavier stack, second key to
   destroy on erasure, no gain.
2. **Download = LOLA-signed URL, not MinIO presigned.** Same
   ≤15-min contract as ISPPr packages: `GET …/uploads/{id}/download`
   302s to `/api/vault-content/{docId}?exp&sig`; `sig` is a transit
   HMAC over `v1|tid|docId|exp` under the tenant's index key
   (`tenant-<id>-idx`, already `hmac` type). The signature IS the
   credential — the content route is auth-free like any presigned
   URL — and it dies with the tenant's keys on erasure.
   `vault_download` is audited at mint.
3. **Per-tenant bucket** `lola-vault-<tid>` with `quarantine/` +
   `clean/` prefixes — the code cannot name another tenant's
   objects (same guarantee as tenant files); erasure deletes the
   bucket. Created by a re-entrant `TenantVaultProvisioner` step in
   `TenantProvisioningService` (mirrors `IspprTenantProvisioner`).
4. **Async scan outbox.** Upload → encrypt-stream → quarantine →
   row `quarantined` + `vault_scan_outbox` → **202**. The worker
   (mirrors `DocOutboxProcessor` — tenant-scoped, backoff→DLQ)
   decrypts to stream → INSTREAM → clean: server-side `CopyObject`
   promote to `clean/` + row `clean`; infected: row `infected` +
   quarantined object deleted (metadata stays as the audit trail —
   no live malware retained even encrypted); ClamAV down: row
   `unavailable`, retry → DLQ.
5. **Allowlist:** pdf, png, jpg/jpeg, tiff, docx — magic-byte
   sniffing, not Content-Type trust. 25 MB cap (`VAULT_MAX_BYTES`),
   multipart streamed sections, `intake` rate-limit policy.
   EICAR reaches the pipeline at the worker/adapter level (staged
   through real encrypt→quarantine), never via the endpoint —
   the allowlist stays intact.
6. **Filename is PII-class** — transit-encrypted on the row,
   returned only to authorized roles, never logged;
   `Content-Disposition` is RFC5987-encoded.

## Surface

- `src/Lola.Application/Vault/` — ports (`IObjectVault`,
  `IVirusScanner`, `IVaultUrlSigner`, `IVaultDocRepository`,
  `ITenantVaultProvisioner`), wire DTOs, handlers, `VaultCipher`.
- `src/Lola.DataPlane/VaultRepository.cs` + `Migrations/0011_vault.sql`
  (`vault_docs`, `vault_scan_outbox`).
- `src/Lola.Infrastructure/Vault/` — `MinioObjectVault`,
  `ClamAvScanner`, `TransitVaultUrlSigner`,
  `TenantVaultProvisioner`, `StubObjectVault`/`StubScanner`.
- `src/Lola.Api/Vault/` — endpoints + `VaultScanWorker`; processor
  drained explicitly in tests like `DocOutboxProcessor`.
- REST only (ADR-0011 — GraphQL never carries binaries; the list/
  detail read model lands REST alongside the upload/download routes
  for M9 parity).

## Phases

- **P1 — seams + schema + cipher + upload ingest (hermetic)**
  (ADR-0007/0004/0011; invariants 1, 3, 4, 10)
  Ports + DTOs; `0011_vault.sql` (`vault_docs`,
  `vault_scan_outbox`); `VaultRepository` (same-tx enqueue,
  request-hash dedupe, status transitions); `VaultCipher` chunked
  AEAD + wrap via `ITenantKeyManager` (round-trip + tamper tests);
  `StubObjectVault`/`StubScanner`; `POST …/uploads` (multipart →
  encrypt-stream → quarantine → 202), `GET …/uploads[/{id}]`;
  `VAULT_ADAPTER=stub`. Tests assert stub-vault bytes are
  ciphertext and allowlist/size rejects hold.
- **P2 — live MinIO + ClamAV pipeline** (ADR-0007; invariants 1, 5)
  `MinioObjectVault`, `ClamAvScanner`, `TenantVaultProvisioner`
  bucket step, `VaultScanProcessor`/`VaultScanWorker`
  (`VAULT_SCAN_*` env), `VAULT_ADAPTER=minio` wiring. `VaultLiveTests`
  (never-skip): ciphertext round-trip, clean→promote, **EICAR→
  infected + never in `clean/`**, ClamAV-down→unavailable retry,
  provisioner creates bucket. CI: `docker run` minio + clamav with
  health gates (mirrors the OpenBao step).
- **P3 — download contract + scoping + lifecycle**
  (invariants 3, 4, 9)
  `TransitVaultUrlSigner`; download 302 + `/api/vault-content/{id}`
  capability route (decrypt-stream → TLS, clean-only → 404/410);
  borrower-own proofs (own-app OK + audited; cross-app 403/404);
  erasure `PurgeVaultAsync` fail-closed before the shred;
  expired/tampered sig → 403; non-clean download → conflict;
  double-download → two audit events; newest-first ordering.
- **P4 — marquee + hardening gate + close-out**
  `VaultMarqueeTests` on the real stack (provision → submit →
  upload → drain → clean → 302 → content fetch → SHA-256 byte
  match → `vault_*` audit spine; EICAR quarantine proof).
  OWASP gate on MPE-73 (multipart limits, decompression — never
  unzipped, capability-URL entropy, INSTREAM bounds, filename
  header injection). README M10 paragraph, `.env.example`
  `VAULT_*` sweep, wiki `Borrower-Document-Vault` page, code-shape
  on MPE-73, branch cleanup, follow-up tickets.

## Acceptance map (MPE-73)

| Ticket acceptance | Proof |
|---|---|
| EICAR quarantined, never reaches clean bucket | `VaultLiveTests.Eicar_*` + marquee (P2/P4) |
| Uploads never stored unencrypted; presigned access expires on schedule | cipher/tamper unit tests + stored-bytes assertions (P1/P2); expired-sig → 403 (P3) |
| Doc list + download via API; classification recorded | endpoint tests (P1/P3) + marquee (P4) |
| Retention hooks into M6 lifecycle | erasure purge step + fail-closed test (P3) |

## Risks / honest flags

- **`unavailable` semantics:** a tenant's docs can sit unscanned if
  ClamAV is down for a long window — downloads refuse non-`clean`
  status, so availability (not safety) is what's at stake; DLQ is
  operator-visible like the other outboxes.
- **Signature binding:** `sig` covers tid+docId+exp only — a leaked
  URL is a 15-min read capability for that one object; same
  exposure class as a MinIO presigned URL, mint is audited.
- **Scan-freshness:** ClamAV signature DB age is not checked
  (compose `clamav:stable` auto-updates daily); a freshness/health
  probe is a flagged follow-up, not MVP scope.
- **Docx is a ZIP** — we never unzip/render, so zip-slip/bombs
  don't apply to the vault itself; downstream consumers inherit
  that caveat (gate note).
