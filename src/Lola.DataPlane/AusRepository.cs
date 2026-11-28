using Lola.Application.Aus;

namespace Lola.DataPlane;

/// AUS store (M25 P2, MPE-316): append-only `aus_results` +
/// `aus_findings` in the tenant file. Rows are never updated —
/// re-submission inserts the next row of the lineage. Raw response
/// XML is written here and ONLY here: it never crosses into events,
/// logs, or posture DTOs (invariant 3).
public sealed class AusRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IAusResultsStore
{
    private static string Now() => DateTimeOffset.UtcNow
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    public async Task<AusResultRow> AddResultAsync(string appId,
        string vendor, string? orderId, string? caseId,
        string recommendation, string? riskClass, string? responseXml,
        string responseHash, int findingsCount, string actor,
        CancellationToken ct)
    {
        var id = $"aus-{Guid.NewGuid():N}";
        var created = Now();
        await ExecuteAsync(
            """
            INSERT INTO aus_results
                (id, application_id, vendor, order_id, case_id,
                 recommendation, risk_class, response_xml, response_hash,
                 findings_count, created_utc, created_by)
            VALUES (@id, @appId, @vendor, @orderId, @caseId,
                    @recommendation, @riskClass, @responseXml, @responseHash,
                    @findingsCount, @created, @actor)
            """,
            new
            {
                id,
                appId,
                vendor,
                orderId,
                caseId,
                recommendation,
                riskClass,
                responseXml,
                responseHash,
                findingsCount,
                created,
                actor
            }, ct);
        return new AusResultRow(id, appId, vendor, orderId, caseId,
            recommendation, riskClass, findingsCount, created, actor);
    }

    public async Task AddFindingAsync(string resultId, string vendorCode,
        string category, string severity, string message,
        string? conditionId, CancellationToken ct)
    {
        await ExecuteAsync(
            """
            INSERT INTO aus_findings
                (id, result_id, vendor_code, category, severity,
                 message, condition_id, created_utc)
            VALUES (@id, @resultId, @vendorCode, @category, @severity,
                    @message, @conditionId, @created)
            """,
            new
            {
                id = $"auf-{Guid.NewGuid():N}",
                resultId,
                vendorCode,
                category,
                severity,
                message,
                conditionId,
                created = Now()
            }, ct);
    }

    public async Task<IReadOnlyList<AusResultRow>> ListResultsAsync(
        string appId, CancellationToken ct)
    {
        var rows = await QueryAsync<ResultRecord>(
            """
            SELECT id AS ResultId, application_id AS ApplicationId,
                   vendor AS Vendor, order_id AS OrderId,
                   case_id AS CaseId, recommendation AS Recommendation,
                   risk_class AS RiskClass, findings_count AS FindingsCount,
                   created_utc AS CreatedUtc, created_by AS CreatedBy
            FROM aus_results
            WHERE application_id=@appId
            ORDER BY created_utc DESC
            """,
            new { appId }, ct);
        return rows.Select(r => new AusResultRow(r.ResultId,
            r.ApplicationId, r.Vendor, r.OrderId, r.CaseId,
            r.Recommendation, r.RiskClass, (int)r.FindingsCount,
            r.CreatedUtc, r.CreatedBy)).ToList();
    }

    public async Task<IReadOnlyList<AusFindingRow>> ListFindingsAsync(
        string resultId, CancellationToken ct)
    {
        var rows = await QueryAsync<FindingRecord>(
            """
            SELECT id AS FindingId, result_id AS ResultId,
                   vendor_code AS VendorCode, category AS Category,
                   severity AS Severity, message AS Message,
                   condition_id AS ConditionId, created_utc AS CreatedUtc
            FROM aus_findings
            WHERE result_id=@resultId
            ORDER BY created_utc
            """,
            new { resultId }, ct);
        return rows.Select(r => new AusFindingRow(r.FindingId,
            r.ResultId, r.VendorCode, r.Category, r.Severity,
            r.Message, r.ConditionId, r.CreatedUtc)).ToList();
    }

    private sealed record ResultRecord(string ResultId,
        string ApplicationId, string Vendor, string? OrderId,
        string? CaseId, string Recommendation, string? RiskClass,
        long FindingsCount, string CreatedUtc, string CreatedBy);

    private sealed record FindingRecord(string FindingId,
        string ResultId, string VendorCode, string Category,
        string Severity, string Message, string? ConditionId,
        string CreatedUtc);
}
