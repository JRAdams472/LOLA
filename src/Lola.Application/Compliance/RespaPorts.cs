namespace Lola.Application.Compliance;

/// RESPA servicing/escrow persistence seam (M23 P2, MPE-310). Rows
/// live in the tenant file — `escrow_analyses` is the append-only
/// analysis read surface (never updated; a re-run inserts a new row);
/// `servicing_notices` carries one row per transfer-notice obligation
/// whose status transitions append `servicing_notice_events` rows.
/// REAL columns materialize as `double` per the M22 Dapper lesson —
/// decimal conversion happens at the domain boundary.
public interface IRespaRepository
{
    /// Insert one append-only analysis row.
    Task<EscrowAnalysisRecord> AddAnalysisAsync(string appId,
        string verdict, decimal? annualDisbursements,
        decimal? cushionTarget, decimal? lowPoint,
        decimal? determinationAmount, bool refundRequired,
        string inputsJson, string detailJson, string actor,
        CancellationToken ct);

    Task<IReadOnlyList<EscrowAnalysisRecord>> ListAnalysesAsync(
        string appId, CancellationToken ct);

    /// Insert one notice row (pending) + its first notice-event.
    Task<ServicingNoticeRecord> AddNoticeAsync(string appId,
        string noticeKind, string transferEffective,
        string transferorLabel, string transfereeLabel,
        string sendBy, string actor, CancellationToken ct);

    Task<ServicingNoticeRecord?> GetNoticeAsync(string noticeId,
        CancellationToken ct);

    Task<IReadOnlyList<ServicingNoticeRecord>> ListNoticesAsync(
        string appId, CancellationToken ct);

    /// Transition a notice's status + append the notice-event row.
    /// Returns the event timestamp — the sent/received date of record.
    Task<string> TransitionNoticeAsync(string noticeId, string status,
        string actor, CancellationToken ct);

    Task<IReadOnlyList<ServicingNoticeEventRecord>> ListNoticeEventsAsync(
        string noticeId, CancellationToken ct);
}

/// One `escrow_analyses` row.
public sealed record EscrowAnalysisRecord(
    string AnalysisId, string ApplicationId, string Verdict,
    double? AnnualDisbursements, double? CushionTarget,
    double? LowPoint, double? DeterminationAmount,
    long RefundRequired, string InputsJson, string DetailJson,
    string Actor, string? CreatedUtc);

/// One `servicing_notices` row.
public sealed record ServicingNoticeRecord(
    string NoticeId, string ApplicationId, string NoticeKind,
    string TransferEffective, string TransferorLabel,
    string TransfereeLabel, string SendBy, string Status,
    string Actor, string? CreatedUtc);

/// One `servicing_notice_events` row — append-only status lineage.
public sealed record ServicingNoticeEventRecord(
    string EventId, string NoticeId, string Status, string Actor,
    string? CreatedUtc);

/// GET …/respa report — the servicing/escrow posture.
public sealed record RespaReportDto(
    EscrowAnalysisDto? LatestAnalysis,
    IReadOnlyList<EscrowAnalysisDto> AnalysisHistory,
    bool AfbaDisclosureRequired,
    IReadOnlyList<ServicingNoticeDto> Notices);

/// One escrow analysis as the wire sees it.
public sealed record EscrowAnalysisDto(
    string AnalysisId, string Verdict, decimal? AnnualDisbursements,
    decimal? CushionTarget, decimal? LowPoint,
    decimal? DeterminationAmount, bool RefundRequired,
    IReadOnlyList<string> MissingInputs, string Actor,
    string? CreatedUtc);

/// One servicing notice with live statutory evaluation.
public sealed record ServicingNoticeDto(
    string NoticeId, string NoticeKind, string TransferEffective,
    string TransferorLabel, string TransfereeLabel,
    string SendBy, string Status,
    /// Computed at read time: pending past deadline / sent late.
    bool Overdue, bool SentLate,
    string? SentUtc, string? ReceivedUtc,
    IReadOnlyList<ServicingNoticeEventDto> Events);

/// One notice-event as the wire sees it.
public sealed record ServicingNoticeEventDto(
    string Status, string Actor, string? CreatedUtc);
