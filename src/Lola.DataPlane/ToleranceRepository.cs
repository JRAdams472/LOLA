using Dapper;
using Lola.Application.Compliance;
using Lola.Application.Security;
using Microsoft.Data.Sqlite;

namespace Lola.DataPlane;

/// TRID tolerance store (M21 P2, MPE-298): `disclosure_versions` +
/// `tolerance_resets` + the `tolerance_classes` registry, all in the
/// tenant file. Version insert stamps any pending resets to the new
/// version in the same transaction — the re-issue and its
/// rebaseline are one fact, never a torn pair.
public sealed class ToleranceRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IToleranceRepository
{
    public async Task<DisclosureVersionRecord> AddVersionAsync(
        string appId, string kind, string evalId, string? docId,
        string? resetOfVersionId, CancellationToken ct)
    {
        var versionId = $"dver-{Guid.NewGuid():N}";
        await using var conn = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)conn
            .BeginTransaction(System.Data.IsolationLevel.Serializable);
        try
        {
            var seq = await conn.QuerySingleAsync<long>(
                """
                SELECT COALESCE(MAX(seq),0)+1 FROM disclosure_versions
                WHERE application_id=@appId AND kind=@kind
                """, new { appId, kind }, tx);
            await conn.ExecuteAsync(
                """
                INSERT INTO disclosure_versions
                    (version_id, application_id, kind, eval_id, doc_id,
                     seq, reset_of_version_id)
                VALUES (@versionId, @appId, @kind, @evalId, @docId,
                        @seq, @resetOf)
                """,
                new
                {
                    versionId,
                    appId,
                    kind,
                    evalId,
                    docId,
                    seq,
                    resetOf = resetOfVersionId
                }, tx);
            // Pending changed circumstances rebase to this issue.
            await conn.ExecuteAsync(
                """
                UPDATE tolerance_resets
                SET rebased_version_id=@versionId
                WHERE application_id=@appId
                  AND rebased_version_id IS NULL
                """, new { versionId, appId }, tx);
            await tx.CommitAsync(ct);
            return new DisclosureVersionRecord(versionId, appId, kind,
                evalId, docId, seq, resetOfVersionId,
                DateTimeOffset.UtcNow
                    .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'"));
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private const string VersionColumns = """
        version_id AS VersionId, application_id AS ApplicationId,
        kind AS Kind, eval_id AS EvalId, doc_id AS DocId,
        seq AS Seq, reset_of_version_id AS ResetOfVersionId,
        created_utc AS CreatedUtc
        """;

    public async Task<IReadOnlyList<DisclosureVersionRecord>>
        ListVersionsAsync(string appId, string kind,
            CancellationToken ct)
        => await QueryAsync<DisclosureVersionRecord>(
            $"""
            SELECT {VersionColumns} FROM disclosure_versions
            WHERE application_id=@appId AND kind=@kind
            ORDER BY seq
            """, new { appId, kind }, ct);

    public async Task<DisclosureVersionRecord?> GetVersionByDocAsync(
        string docId, CancellationToken ct)
        => await QuerySingleOrDefaultAsync<DisclosureVersionRecord>(
            $"SELECT {VersionColumns} FROM disclosure_versions " +
            "WHERE doc_id=@docId", new { docId }, ct);

    public async Task<IReadOnlyDictionary<string, ToleranceClassRecord>>
        GetClassesAsync(CancellationToken ct)
    {
        var rows = await QueryAsync<ToleranceClassRecord>(
            """
            SELECT fee_code AS FeeCode, bucket AS Bucket,
                   shoppable AS Shoppable
            FROM tolerance_classes
            """, null, ct);
        return rows.ToDictionary(r => r.FeeCode,
            StringComparer.Ordinal);
    }

    public async Task<ToleranceResetRecord> AddResetAsync(
        string appId, string reasonCode, string feeCodesJson,
        string actor, CancellationToken ct)
    {
        var resetId = $"tres-{Guid.NewGuid():N}";
        var created = DateTimeOffset.UtcNow
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
        await ExecuteAsync(
            """
            INSERT INTO tolerance_resets
                (reset_id, application_id, reason_code,
                 affected_fee_codes_json, actor, created_utc)
            VALUES (@resetId, @appId, @reason, @codes, @actor, @created)
            """,
            new
            {
                resetId,
                appId,
                reason = reasonCode,
                codes = feeCodesJson,
                actor,
                created
            }, ct);
        return new ToleranceResetRecord(resetId, appId, reasonCode,
            feeCodesJson, null, actor, created);
    }

    public async Task<IReadOnlyList<ToleranceResetRecord>>
        ListResetsAsync(string appId, CancellationToken ct)
        => await QueryAsync<ToleranceResetRecord>(
            """
            SELECT reset_id AS ResetId,
                   application_id AS ApplicationId,
                   reason_code AS ReasonCode,
                   affected_fee_codes_json AS AffectedFeeCodesJson,
                   rebased_version_id AS RebasedVersionId,
                   actor AS Actor, created_utc AS CreatedUtc
            FROM tolerance_resets WHERE application_id=@appId
            ORDER BY created_utc
            """, new { appId }, ct);

    public async Task StampResetAsync(string resetId,
        string rebasedVersionId, CancellationToken ct)
        => await ExecuteAsync(
            """
            UPDATE tolerance_resets SET rebased_version_id=@vid
            WHERE reset_id=@resetId AND rebased_version_id IS NULL
            """, new { vid = rebasedVersionId, resetId }, ct);
}
