using Lola.Domain.Compliance;

namespace Lola.Application.Compliance;

/// Tolerance persistence seam (M21 P2, MPE-298). Rows live in the
/// tenant file — `disclosure_versions` binds each generated le/cd
/// package to the fee eval it rendered; `tolerance_resets` records
/// changed-circumstance rebaselines. Both append-only (resets gain
/// `rebased_version_id` once the re-issue lands — a stamp, not a
/// mutation of history).
public interface IToleranceRepository
{
    /// Insert a version row with `seq` = next per (app, kind).
    Task<DisclosureVersionRecord> AddVersionAsync(
        string appId, string kind, string evalId, string? docId,
        string? resetOfVersionId, CancellationToken ct);

    Task<IReadOnlyList<DisclosureVersionRecord>> ListVersionsAsync(
        string appId, string kind, CancellationToken ct);

    Task<DisclosureVersionRecord?> GetVersionByDocAsync(
        string docId, CancellationToken ct);

    /// `tolerance_classes` registry as a fee_code→class map.
    Task<IReadOnlyDictionary<string, ToleranceClassRecord>>
        GetClassesAsync(CancellationToken ct);

    Task<ToleranceResetRecord> AddResetAsync(
        string appId, string reasonCode, string feeCodesJson,
        string actor, CancellationToken ct);

    Task<IReadOnlyList<ToleranceResetRecord>> ListResetsAsync(
        string appId, CancellationToken ct);

    /// Stamp a pending reset (rebased_version_id IS NULL) to the
    /// version that became its fees' new baseline.
    Task StampResetAsync(string resetId, string rebasedVersionId,
        CancellationToken ct);
}

/// One `disclosure_versions` row.
public sealed record DisclosureVersionRecord(
    string VersionId, string ApplicationId, string Kind,
    string EvalId, string? DocId, long Seq,
    string? ResetOfVersionId, string? CreatedUtc);

/// One `tolerance_resets` row.
public sealed record ToleranceResetRecord(
    string ResetId, string ApplicationId, string ReasonCode,
    string AffectedFeeCodesJson, string? RebasedVersionId,
    string Actor, string? CreatedUtc);

/// One `tolerance_classes` registry row.
public sealed record ToleranceClassRecord(
    string FeeCode, string Bucket, long Shoppable);

/// Package kind → disclosure kind mapping ('le-package'→'le').
public static class DisclosureKind
{
    public static string? Of(string packageKind) => packageKind switch
    {
        "le-package" => "le",
        "cd-package" => "cd",
        _ => null,
    };
}

/// GET …/tolerance report — version lineage, the latest version's
/// per-bucket variances + violations, resets, and the open cure
/// condition if any.
public sealed record ToleranceReportDto(
    IReadOnlyList<DisclosureVersionDto> Versions,
    IReadOnlyList<ToleranceResetDto> Resets,
    IReadOnlyList<BucketVarianceDto> Buckets,
    IReadOnlyList<ViolationDto> Violations,
    string? OpenCureConditionId);

public sealed record DisclosureVersionDto(
    string VersionId, string Kind, string EvalId, string? DocId,
    long Seq, string? ResetOfVersionId, string? CreatedUtc);

public sealed record ToleranceResetDto(
    string ResetId, string ReasonCode,
    IReadOnlyList<string> AffectedFeeCodes, string? RebasedVersionId,
    string Actor, string? CreatedUtc);

public sealed record BucketVarianceDto(
    string Bucket, decimal Baseline, decimal Compare,
    IReadOnlyList<LineVarianceDto> Lines);

public sealed record LineVarianceDto(
    string FeeCode, decimal Baseline, decimal Compare, decimal Delta);

public sealed record ViolationDto(
    string Bucket, string? FeeCode, decimal Baseline, decimal Compare,
    decimal Allowed, string Rule);

/// Statutory changed-circumstance reason codes — §1026.19(e)(3)(iv).
public static class ToleranceResetReasons
{
    public const string ChangedInfo = "changed_info";
    public const string BorrowerRequest = "borrower_request";
    public const string RateLock = "rate_lock";
    public const string Extraordinary = "extraordinary";
    public const string Eligibility = "eligibility";
    public const string DelayedSettlement = "delayed_settlement";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string>
        {
            ChangedInfo, BorrowerRequest, RateLock,
            Extraordinary, Eligibility, DelayedSettlement,
        };
}
