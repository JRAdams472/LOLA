using Dapper;
using Lola.Application.Security;
using Lola.Application.Vault;
using Microsoft.Data.Sqlite;

namespace Lola.DataPlane;

/// Tenant-file vault store (MPE-200): `vault_docs` registry +
/// `vault_scan_outbox` work queue. Enqueue is doc+row atomically,
/// scan verdicts are outbox+doc atomically — same transaction
/// discipline as DocRepository/FeeRepository. `filename` is
/// PII-class so it is `vault:` ciphertext at rest — encrypted on
/// enqueue, decrypted on read; plaintext only ever lives in memory.
public sealed class VaultRepository(TenantConnectionFactory factory,
    IFieldCipher cipher)
    : TenantRepository(factory), IVaultDocRepository
{
    public async Task<VaultEnqueueResult> EnqueueAsync(
        VaultDocRecord doc, string outboxId,
        CancellationToken ct = default)
    {
        var filenameCipher = await cipher.EncryptAsync(doc.Filename, ct);
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            var existing = await conn.QuerySingleOrDefaultAsync<string>(
                """
                SELECT vault_doc_id FROM vault_docs
                WHERE request_hash=@hash
                """, new { hash = doc.RequestHash }, tx);
            if (existing is not null)
            {
                await tx.CommitAsync(ct);
                return new VaultEnqueueResult(existing, true);
            }

            await conn.ExecuteAsync(
                """
                INSERT INTO vault_docs
                    (vault_doc_id, application_id, classification,
                     filename, content_type, size_bytes, sha256,
                     wrapped_dek, adapter, s3_key, status,
                     uploaded_by, request_hash)
                VALUES (@docId, @appId, @classification,
                        @filename, @contentType, @size, @sha256,
                        @dek, @adapter, @key, @status,
                        @actor, @hash)
                """,
                new
                {
                    docId = doc.VaultDocId,
                    appId = doc.ApplicationId,
                    classification = doc.Classification,
                    filename = filenameCipher,
                    contentType = doc.ContentType,
                    size = doc.SizeBytes,
                    sha256 = doc.Sha256,
                    dek = doc.WrappedDek,
                    adapter = doc.Adapter,
                    key = doc.S3Key,
                    status = doc.Status,
                    actor = doc.Actor,
                    hash = doc.RequestHash
                }, tx);
            await conn.ExecuteAsync(
                """
                INSERT INTO vault_scan_outbox
                    (outbox_id, vault_doc_id, request_hash)
                VALUES (@outboxId, @docId, @hash)
                """,
                new
                {
                    outboxId,
                    docId = doc.VaultDocId,
                    hash = doc.RequestHash
                }, tx);
            await tx.CommitAsync(ct);
            return new VaultEnqueueResult(doc.VaultDocId, false);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private const string DocColumns = """
        vault_doc_id AS VaultDocId, application_id AS ApplicationId,
        classification AS Classification, filename AS Filename,
        content_type AS ContentType, size_bytes AS SizeBytes,
        sha256 AS Sha256, wrapped_dek AS WrappedDek,
        adapter AS Adapter, s3_key AS S3Key, status AS Status,
        error AS Error, uploaded_by AS Actor,
        created_utc AS CreatedUtc, scanned_utc AS ScannedUtc,
        request_hash AS RequestHash
        """;

    private sealed record CipherRow(
        string VaultDocId, string ApplicationId, string Classification,
        string Filename, string ContentType, long SizeBytes,
        string Sha256, string WrappedDek, string Adapter, string S3Key,
        string Status, string? Error, string Actor,
        string CreatedUtc, string? ScannedUtc, string RequestHash);

    private async Task<VaultDocRecord> DecryptAsync(
        CipherRow r, CancellationToken ct)
        => new(r.VaultDocId, r.ApplicationId, r.Classification,
            await cipher.DecryptAsync(r.Filename, ct), r.ContentType,
            r.SizeBytes, r.Sha256, r.WrappedDek, r.Adapter, r.S3Key,
            r.Status, r.Error, r.Actor, r.CreatedUtc, r.ScannedUtc,
            r.RequestHash);

    public async Task<VaultDocRecord?> GetAsync(string vaultDocId,
        CancellationToken ct = default)
    {
        var row = await QuerySingleOrDefaultAsync<CipherRow>(
            $"SELECT {DocColumns} FROM vault_docs WHERE vault_doc_id=@id",
            new { id = vaultDocId }, ct);
        return row is null ? null : await DecryptAsync(row, ct);
    }

    public async Task<IReadOnlyList<VaultDocRecord>> ListAsync(
        string appId, CancellationToken ct = default)
    {
        var rows = (await QueryAsync<CipherRow>(
            $"""
            SELECT {DocColumns} FROM vault_docs
            WHERE application_id=@appId
            ORDER BY created_utc DESC, rowid DESC
            """, new { appId }, ct)).ToList();
        var result = new List<VaultDocRecord>(rows.Count);
        foreach (var r in rows)
            result.Add(await DecryptAsync(r, ct));
        return result;
    }

    public async Task<IReadOnlyList<VaultScanOutboxRow>>
        ListDueScansAsync(string nowUtc, int limit,
            CancellationToken ct = default)
        => await QueryAsync<VaultScanOutboxRow>(
            """
            SELECT outbox_id AS OutboxId, vault_doc_id AS VaultDocId,
                   request_hash AS RequestHash, status AS Status,
                   attempts AS Attempts,
                   next_retry_utc AS NextRetryUtc,
                   last_error AS LastError
            FROM vault_scan_outbox
            WHERE status='pending'
              AND (next_retry_utc IS NULL OR next_retry_utc <= @nowUtc)
            ORDER BY created_utc LIMIT @limit
            """, new { nowUtc, limit }, ct);

    public async Task MarkScanningAsync(string outboxId,
        string nextRetryUtc, CancellationToken ct = default)
        => await ExecuteAsync(
            """
            UPDATE vault_scan_outbox
            SET next_retry_utc=@nextRetryUtc,
                updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE outbox_id=@outboxId
            """, new { outboxId, nextRetryUtc }, ct);

    public async Task CompleteScanAsync(string outboxId,
        string vaultDocId, string cleanKey, string scannedUtc,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            await conn.ExecuteAsync(
                """
                UPDATE vault_scan_outbox
                SET status='done',
                    updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE outbox_id=@outboxId
                """, new { outboxId }, tx);
            await conn.ExecuteAsync(
                """
                UPDATE vault_docs
                SET status='clean', s3_key=@cleanKey,
                    scanned_utc=@scannedUtc
                WHERE vault_doc_id=@docId
                """, new
                {
                    docId = vaultDocId,
                    cleanKey,
                    scannedUtc
                }, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task MarkInfectedAsync(string outboxId,
        string vaultDocId, string signature, string scannedUtc,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            await conn.ExecuteAsync(
                """
                UPDATE vault_scan_outbox
                SET status='done',
                    updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE outbox_id=@outboxId
                """, new { outboxId }, tx);
            await conn.ExecuteAsync(
                """
                UPDATE vault_docs
                SET status='infected', error=@signature,
                    scanned_utc=@scannedUtc
                WHERE vault_doc_id=@docId
                """, new
                {
                    docId = vaultDocId,
                    signature,
                    scannedUtc
                }, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task MarkUnavailableAsync(string outboxId,
        string vaultDocId, string error,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            await conn.ExecuteAsync(
                """
                UPDATE vault_scan_outbox
                SET status='dlq', last_error=@error,
                    updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE outbox_id=@outboxId
                """, new { outboxId, error }, tx);
            await conn.ExecuteAsync(
                """
                UPDATE vault_docs
                SET status='unavailable', error=@error
                WHERE vault_doc_id=@docId
                """, new { docId = vaultDocId, error }, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// Retry requeue — attempts unchanged (a stalled scan isn't a
    /// failure of the scan itself); only engine errors consume the
    /// retry budget.
    public async Task RescheduleScanAsync(string outboxId,
        long attempts, string nextRetryUtc, string error,
        CancellationToken ct = default)
        => await ExecuteAsync(
            """
            UPDATE vault_scan_outbox
            SET attempts=@attempts, next_retry_utc=@nextRetryUtc,
                last_error=@error,
                updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE outbox_id=@outboxId
            """, new { outboxId, attempts, nextRetryUtc, error }, ct);
}
