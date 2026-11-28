namespace Lola.Application.Aus;

/// Tenant-file store for append-only AUS results + findings (M25).
/// Raw response XML is PII-bearing and never leaves the tenant
/// boundary — the store is the boundary.
public interface IAusResultsStore
{
    Task<AusResultRow> AddResultAsync(string appId, string vendor,
        string? orderId, string? caseId, string recommendation,
        string? riskClass, string? responseXml, string responseHash,
        int findingsCount, string actor, CancellationToken ct);

    Task AddFindingAsync(string resultId, string vendorCode,
        string category, string severity, string message,
        string? conditionId, CancellationToken ct);

    Task<IReadOnlyList<AusResultRow>> ListResultsAsync(string appId,
        CancellationToken ct);

    Task<IReadOnlyList<AusFindingRow>> ListFindingsAsync(string resultId,
        CancellationToken ct);
}

public sealed record AusResultRow(
    string ResultId, string ApplicationId, string Vendor,
    string? OrderId, string? CaseId, string Recommendation,
    string? RiskClass, int FindingsCount, string CreatedUtc,
    string CreatedBy);

public sealed record AusFindingRow(
    string FindingId, string ResultId, string VendorCode,
    string Category, string Severity, string Message,
    string? ConditionId, string CreatedUtc);

/// Posture DTO — latest per vendor + findings + append-only lineage.
public sealed record AusVendorPosture(
    string Vendor, string Recommendation, string? RiskClass,
    string? CaseId, string CreatedUtc, int FindingsCount,
    IReadOnlyList<AusFindingDto> Findings);

public sealed record AusFindingDto(
    string VendorCode, string Category, string Severity,
    string Message, string? ConditionId);

public sealed record AusReportDto(
    IReadOnlyList<AusVendorPosture> Vendors,
    IReadOnlyList<AusLineageEntry> Lineage);

public sealed record AusLineageEntry(
    string ResultId, string Vendor, string Recommendation,
    string CreatedUtc, int FindingsCount);
