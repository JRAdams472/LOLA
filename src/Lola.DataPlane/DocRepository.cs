using Dapper;
using Lola.Application.Documents;
using Lola.Application.Security;
using Microsoft.Data.Sqlite;

namespace Lola.DataPlane;

/// Tenant-file document store (MPE-190): `documents` registry +
/// `doc_outbox` work queue. Enqueue is doc+row atomically, completion
/// is outbox+doc atomically — same transaction discipline as
/// FeeRepository. `payload_json` carries the manifest-bounded
/// disclosure payload (template-required PII), so it is `vault:`
/// ciphertext at rest — encrypted on enqueue, decrypted on the
/// worker's due-row read; plaintext only ever lives in memory.
public sealed class DocRepository(TenantConnectionFactory factory,
    IFieldCipher cipher)
    : TenantRepository(factory), IDocumentRepository
{
    public async Task<DocEnqueueResult> EnqueueAsync(DocRecord doc,
        string outboxId, string kind, string payloadJson,
        CancellationToken ct = default)
    {
        var ciphertext = await cipher.EncryptAsync(payloadJson, ct);
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            var existing = await conn.QuerySingleOrDefaultAsync<string>(
                "SELECT doc_id FROM doc_outbox WHERE request_hash=@hash",
                new { hash = doc.RequestHash }, tx);
            if (existing is not null)
            {
                await tx.CommitAsync(ct);
                return new DocEnqueueResult(existing, true);
            }

            await conn.ExecuteAsync(
                """
                INSERT INTO documents
                    (doc_id, application_id, kind, package_name,
                     adapter, request_hash, status, actor)
                VALUES (@docId, @appId, @kind, @packageName,
                        @adapter, @hash, 'pending', @actor)
                """,
                new
                {
                    docId = doc.DocId,
                    appId = doc.ApplicationId,
                    kind = doc.Kind,
                    packageName = doc.PackageName,
                    adapter = doc.Adapter,
                    hash = doc.RequestHash,
                    actor = doc.Actor
                }, tx);
            await conn.ExecuteAsync(
                """
                INSERT INTO doc_outbox
                    (outbox_id, kind, doc_id, request_hash, payload_json)
                VALUES (@outboxId, @kind, @docId, @hash, @payload)
                """,
                new
                {
                    outboxId,
                    kind,
                    docId = doc.DocId,
                    hash = doc.RequestHash,
                    payload = ciphertext
                }, tx);
            await tx.CommitAsync(ct);
            return new DocEnqueueResult(doc.DocId, false);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private const string DocColumns = """
        doc_id AS DocId, application_id AS ApplicationId,
        kind AS Kind, package_name AS PackageName,
        adapter AS Adapter, isppr_request_id AS RequestId,
        filename AS Filename, status AS Status, error AS Error,
        actor AS Actor, created_utc AS CreatedUtc,
        completed_utc AS CompletedUtc, request_hash AS RequestHash
        """;

    public async Task<DocRecord?> GetDocAsync(string docId,
        CancellationToken ct = default)
        => await QuerySingleOrDefaultAsync<DocRecord>(
            $"SELECT {DocColumns} FROM documents WHERE doc_id=@docId",
            new { docId }, ct);

    public async Task<IReadOnlyList<DocRecord>> ListDocsAsync(
        string appId, CancellationToken ct = default)
        => await QueryAsync<DocRecord>(
            $"""
            SELECT {DocColumns} FROM documents
            WHERE application_id=@appId ORDER BY created_utc, rowid
            """, new { appId }, ct);

    /// Due rows join the doc row for the vendor request id — null
    /// means the row hasn't been submitted yet (generate phase);
    /// non-null means poll phase. Payload comes back decrypted.
    public async Task<IReadOnlyList<DocOutboxRow>> ListDueOutboxAsync(
        string nowUtc, int limit, CancellationToken ct = default)
    {
        var rows = (await QueryAsync<OutboxJoin>(
            """
            SELECT o.outbox_id AS OutboxId, o.kind AS Kind,
                   o.doc_id AS DocId, o.request_hash AS RequestHash,
                   o.payload_json AS PayloadJson, o.status AS Status,
                   o.attempts AS Attempts,
                   o.next_retry_utc AS NextRetryUtc,
                   o.last_error AS LastError,
                   d.isppr_request_id AS RequestId
            FROM doc_outbox o JOIN documents d ON d.doc_id=o.doc_id
            WHERE o.status='pending'
              AND (o.next_retry_utc IS NULL OR o.next_retry_utc <= @nowUtc)
            ORDER BY o.created_utc LIMIT @limit
            """, new { nowUtc, limit }, ct)).ToList();
        var result = new List<DocOutboxRow>(rows.Count);
        foreach (var r in rows)
            result.Add(new DocOutboxRow(r.OutboxId, r.Kind, r.DocId,
                r.RequestHash, await cipher.DecryptAsync(r.PayloadJson, ct),
                r.Status, r.Attempts, r.NextRetryUtc, r.LastError,
                r.RequestId));
        return result;
    }
    private sealed record OutboxJoin(
        string OutboxId, string Kind, string DocId, string RequestHash,
        string PayloadJson, string Status, long Attempts,
        string? NextRetryUtc, string? LastError, string? RequestId);

    public async Task MarkGeneratingAsync(string outboxId, string docId,
        string requestId, string nextRetryUtc,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            await conn.ExecuteAsync(
                """
                UPDATE documents
                SET status='generating', isppr_request_id=@requestId
                WHERE doc_id=@docId
                """, new { docId, requestId }, tx);
            await conn.ExecuteAsync(
                """
                UPDATE doc_outbox
                SET next_retry_utc=@nextRetryUtc,
                    updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE outbox_id=@outboxId
                """, new { outboxId, nextRetryUtc }, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task CompleteAsync(string outboxId, string docId,
        string requestId, string? filename, string completedUtc,
        CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            await conn.ExecuteAsync(
                """
                UPDATE doc_outbox
                SET status='done', updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE outbox_id=@outboxId
                """, new { outboxId }, tx);
            await conn.ExecuteAsync(
                """
                UPDATE documents
                SET status='completed', filename=@filename,
                    isppr_request_id=@requestId,
                    completed_utc=@completedUtc
                WHERE doc_id=@docId
                """, new { docId, requestId, filename, completedUtc }, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    /// Poll requeue — attempts unchanged (a still-rendering package
    /// isn't a failure); only errors consume the retry budget.
    public async Task RescheduleAsync(string outboxId, string docId,
        long attempts, string nextRetryUtc, string error,
        CancellationToken ct = default)
        => await ExecuteAsync(
            """
            UPDATE doc_outbox
            SET attempts=@attempts, next_retry_utc=@nextRetryUtc,
                last_error=@error,
                updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
            WHERE outbox_id=@outboxId
            """, new { outboxId, attempts, nextRetryUtc, error }, ct);

    public async Task DeadLetterAsync(string outboxId, string docId,
        long attempts, string error, CancellationToken ct = default)
    {
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            await conn.ExecuteAsync(
                """
                UPDATE doc_outbox
                SET status='dlq', attempts=@attempts, last_error=@error,
                    updated_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE outbox_id=@outboxId
                """, new { outboxId, attempts, error }, tx);
            await conn.ExecuteAsync(
                """
                UPDATE documents
                SET status='failed', error=@error,
                    completed_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now')
                WHERE doc_id=@docId
                """, new { docId, error }, tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }
}
