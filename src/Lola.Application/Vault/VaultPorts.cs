namespace Lola.Application.Vault;

/// Borrower-document-vault object-store seam (ADR-0007, MPE-73).
/// Core logic talks ONLY to this interface — MinIO is the first
/// implementation. Tenant binding is ambient (TenantContext): the
/// adapter maps the resolved tenant to its own bucket
/// (`lola-vault-<tid>`), so no code path can name another tenant's
/// objects — the same guarantee as the per-tenant SQLCipher file.
///
/// Everything that crosses this boundary is ciphertext: the caller
/// hands a sealing stream to PutAsync (app-layer envelope, ADR-0004)
/// and OpenAsync returns plaintext only in memory. The vault never
/// sees plaintext bytes — invariant 1 covers the object store too.
public interface IObjectVault
{
    /// Adapter identity stamped onto vault doc rows ("stub", "minio").
    string Adapter { get; }

    /// Write `content` under `key` (e.g. `quarantine/<docId>`). The
    /// stream is pulled lazily — callers wrap it in VaultCipher's
    /// sealing stream so bytes crossing are ciphertext.
    Task PutAsync(string key, Stream content,
        CancellationToken ct = default);

    /// Open `key` for reading (ciphertext bytes back to the caller).
    Task<Stream> GetAsync(string key, CancellationToken ct = default);

    /// Server-side copy (quarantine → clean promotion — bytes move
    /// without re-encryption since the ciphertext + DEK are unchanged).
    Task CopyAsync(string sourceKey, string destKey,
        CancellationToken ct = default);

    /// Delete one object (infected quarantined upload).
    Task DeleteAsync(string key, CancellationToken ct = default);

    /// Destroy every object for the current tenant (erasure path —
    /// MinIO impl deletes the bucket contents then the bucket).
    Task PurgeTenantAsync(CancellationToken ct = default);
}

/// AV seam (ADR-0007) — ClamAV `zINSTREAM` first. Implementations
/// classify a plaintext stream the worker hands them; plaintext never
/// reaches durable storage anywhere.
public interface IVirusScanner
{
    string Adapter { get; }

    Task<ScanVerdict> ScanAsync(Stream content,
        CancellationToken ct = default);
}

/// Verdict of one scan. `Signature` is the engine's detection name
/// (e.g. Eicar-Signature) — never file contents.
public sealed record ScanVerdict(ScanStatus Status, string? Signature);

public enum ScanStatus { Clean, Infected, Unavailable }

/// Signed-URL mint for the download route (MPE-202 impl): the
/// signature IS the credential — the content route is auth-free like
/// any presigned URL. `sig` binds v1|tenant|docId|exp under the
/// tenant's transit hmac key, so a leaked URL is a ≤15-min read
/// capability for one object and dies with the tenant's keys.
public interface IVaultUrlSigner
{
    /// Opaque signature for `v1|{docId}|{expUtc:O}` — hex/base64.
    Task<string> SignAsync(string docId, DateTimeOffset expUtc,
        CancellationToken ct = default);

    /// Constant-time verify; false on tamper (never throws).
    Task<bool> VerifyAsync(string docId, DateTimeOffset expUtc,
        string signature, CancellationToken ct = default);
}

/// One persisted vault row (tenant-file `vault_docs`, MPE-200).
/// Status: quarantined → clean | infected | unavailable. `Filename`
/// is PII-class — plaintext at this boundary, `vault:` ciphertext at
/// rest inside the repository. `S3Key` tracks the object's current
/// prefix (quarantine/ → clean/ on promotion).
public sealed record VaultDocRecord(
    string VaultDocId, string ApplicationId, string Classification,
    string Filename, string ContentType, long SizeBytes,
    string Sha256, string WrappedDek, string Adapter, string S3Key,
    string Status, string? Error, string Actor,
    string CreatedUtc, string? ScannedUtc, string RequestHash);

/// One queued scan (tenant-file `vault_scan_outbox`) — same semantics
/// as doc_outbox/fee_outbox: dedupe by request_hash, bounded attempts
/// with backoff, dead-letter instead of silent loss. Payload-free:
/// the object itself is the payload — the row only names it.
public sealed record VaultScanOutboxRow(
    string OutboxId, string VaultDocId, string RequestHash,
    string Status, long Attempts, string? NextRetryUtc,
    string? LastError);

/// Enqueue outcome — a duplicated upload returns the ORIGINAL doc id.
public sealed record VaultEnqueueResult(string VaultDocId,
    bool Deduplicated);

/// Vault persistence seam (MPE-200). Same transaction discipline as
/// IDocumentRepository — enqueue is row+outbox atomically, scan
/// verdicts are outbox+doc atomically. The repository owns the
/// filename cipher boundary.
public interface IVaultDocRepository
{
    Task<VaultEnqueueResult> EnqueueAsync(VaultDocRecord doc,
        string outboxId, CancellationToken ct = default);

    Task<VaultDocRecord?> GetAsync(string vaultDocId,
        CancellationToken ct = default);
    Task<IReadOnlyList<VaultDocRecord>> ListAsync(string appId,
        CancellationToken ct = default);

    Task<IReadOnlyList<VaultScanOutboxRow>> ListDueScansAsync(
        string nowUtc, int limit, CancellationToken ct = default);

    /// Scan started — outbox rescheduled for the next poll window in
    /// case the scan itself stalls (keeps the claim honest).
    Task MarkScanningAsync(string outboxId, string nextRetryUtc,
        CancellationToken ct = default);

    /// Terminal clean — outbox done + doc status `clean`, s3_key moved
    /// to the clean prefix, scanned_utc stamped.
    Task CompleteScanAsync(string outboxId, string vaultDocId,
        string cleanKey, string scannedUtc, CancellationToken ct = default);

    /// Terminal infected — outbox done + doc status `infected` with
    /// the engine signature as error detail.
    Task MarkInfectedAsync(string outboxId, string vaultDocId,
        string signature, string scannedUtc,
        CancellationToken ct = default);

    /// Terminal unavailable — outbox dead-lettered + doc status
    /// `unavailable` (ClamAV couldn't scan; nothing is promoted).
    Task MarkUnavailableAsync(string outboxId, string vaultDocId,
        string error, CancellationToken ct = default);

    Task RescheduleScanAsync(string outboxId, long attempts,
        string nextRetryUtc, string error,
        CancellationToken ct = default);
}

/// Wire-facing registry row.
public sealed record VaultDocDto(
    string VaultDocId, string ApplicationId, string Classification,
    string Filename, string ContentType, long SizeBytes,
    string Sha256, string Status, string? Error,
    string? CreatedUtc, string? ScannedUtc);

/// Provisioning step seam (MPE-201 impl): ensures the tenant's vault
/// bucket exists (re-entrant like every provisioning step) and purges
/// it on erasure — the purge runs BEFORE the key shred, fail-closed.
public interface ITenantVaultProvisioner
{
    Task EnsureAsync(Domain.Identity.TenantRecord tenant,
        CancellationToken ct = default);

    /// Objects → bucket delete. Idempotent; throws on failure so the
    /// erasure flow aborts retryable instead of orphaning storage.
    Task PurgeAsync(Domain.Identity.TenantRecord tenant,
        CancellationToken ct = default);
}

/// The upload crossed the size cap mid-stream — the endpoint maps it
/// to 413; the caller deletes any partial object.
public sealed class VaultTooLargeException(long limit)
    : Exception($"upload exceeds the {limit}-byte limit");
