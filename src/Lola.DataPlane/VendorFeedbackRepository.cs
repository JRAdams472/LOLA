using Dapper;
using Lola.Application.Vendors;

namespace Lola.DataPlane;

/// Tenant-file vendor feedback ledger (M17 P1) — the replay dedupe
/// record for signed validation webhooks. `UNIQUE (source,
/// feedback_id)` carries the dedupe: `INSERT OR IGNORE` returns 0
/// affected rows on a replay, which is the whole contract — callers
/// never read-then-write.
public sealed class VendorFeedbackRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IVendorFeedbackRepository
{
    private const string Cols = """
        source AS Source, application_id AS ApplicationId,
        feedback_id AS FeedbackId, finding AS Finding,
        severity AS Severity, linked_entity AS LinkedEntity,
        linked_entity_id AS LinkedEntityId, received_utc AS ReceivedUtc
        """;

    public async Task<bool> TryRecordAsync(VendorFeedbackReceipt receipt,
        CancellationToken ct = default)
        => await ExecuteAsync(
            """
            INSERT OR IGNORE INTO vendor_feedback
                (feedback_id, source, application_id, finding, severity,
                 linked_entity, linked_entity_id)
            VALUES (@feedbackId, @source, @appId, @finding, @severity,
                    @linkedEntity, @linkedEntityId)
            """,
            new
            {
                feedbackId = receipt.FeedbackId,
                source = receipt.Source,
                appId = receipt.ApplicationId,
                finding = receipt.Finding,
                severity = receipt.Severity,
                linkedEntity = receipt.LinkedEntity,
                linkedEntityId = receipt.LinkedEntityId
            }, ct) == 1;

    public async Task<VendorFeedbackRow?> GetAsync(string source,
        string feedbackId, CancellationToken ct = default)
        => await QuerySingleOrDefaultAsync<VendorFeedbackRow>(
            $"SELECT {Cols} FROM vendor_feedback"
            + " WHERE source = @source AND feedback_id = @feedbackId",
            new { source, feedbackId }, ct);

    public async Task<IReadOnlyList<VendorFeedbackRow>>
        ListByApplicationAsync(string applicationId,
            CancellationToken ct = default)
        => await QueryAsync<VendorFeedbackRow>(
            $"SELECT {Cols} FROM vendor_feedback"
            + " WHERE application_id = @appId ORDER BY seq",
            new { appId = applicationId }, ct);
}
