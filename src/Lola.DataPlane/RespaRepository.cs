using Lola.Application.Compliance;

namespace Lola.DataPlane;

/// RESPA servicing/escrow store (M23 P2, MPE-310): `escrow_analyses`
/// append-only rows + `servicing_notices` with their
/// `servicing_notice_events` lineage, all in the tenant file.
/// Analysis rows are never updated — a re-run inserts the next row
/// of the lineage; notice status transitions append event rows.
public sealed class RespaRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IRespaRepository
{
    private static string Now() => DateTimeOffset.UtcNow
        .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");

    public async Task<EscrowAnalysisRecord> AddAnalysisAsync(string appId,
        string verdict, decimal? annualDisbursements,
        decimal? cushionTarget, decimal? lowPoint,
        decimal? determinationAmount, bool refundRequired,
        string inputsJson, string detailJson, string actor,
        CancellationToken ct)
    {
        var analysisId = $"esc-{Guid.NewGuid():N}";
        var created = Now();
        await ExecuteAsync(
            """
            INSERT INTO escrow_analyses
                (analysis_id, application_id, verdict,
                 annual_disbursements, cushion_target, low_point,
                 determination_amount, refund_required,
                 inputs_json, detail_json, actor, created_utc)
            VALUES (@analysisId, @appId, @verdict,
                    @annualDisbursements, @cushionTarget, @lowPoint,
                    @determinationAmount, @refundRequired,
                    @inputsJson, @detailJson, @actor, @created)
            """,
            new
            {
                analysisId,
                appId,
                verdict,
                annualDisbursements,
                cushionTarget,
                lowPoint,
                determinationAmount,
                refundRequired = refundRequired ? 1 : 0,
                inputsJson,
                detailJson,
                actor,
                created
            }, ct);
        return new EscrowAnalysisRecord(analysisId, appId, verdict,
            (double?)annualDisbursements, (double?)cushionTarget,
            (double?)lowPoint, (double?)determinationAmount,
            refundRequired ? 1 : 0, inputsJson, detailJson,
            actor, created);
    }

    private const string AnalysisColumns = """
        analysis_id AS AnalysisId, application_id AS ApplicationId,
        verdict AS Verdict,
        annual_disbursements AS AnnualDisbursements,
        cushion_target AS CushionTarget, low_point AS LowPoint,
        determination_amount AS DeterminationAmount,
        refund_required AS RefundRequired,
        inputs_json AS InputsJson, detail_json AS DetailJson,
        actor AS Actor, created_utc AS CreatedUtc
        """;

    public async Task<IReadOnlyList<EscrowAnalysisRecord>>
        ListAnalysesAsync(string appId, CancellationToken ct)
        => await QueryAsync<EscrowAnalysisRecord>(
            $"""
            SELECT {AnalysisColumns} FROM escrow_analyses
            WHERE application_id=@appId
            ORDER BY created_utc DESC, rowid DESC
            """, new { appId }, ct);

    public async Task<ServicingNoticeRecord> AddNoticeAsync(string appId,
        string noticeKind, string transferEffective,
        string transferorLabel, string transfereeLabel,
        string sendBy, string actor, CancellationToken ct)
    {
        var noticeId = $"ntc-{Guid.NewGuid():N}";
        var created = Now();
        await ExecuteAsync(
            """
            INSERT INTO servicing_notices
                (notice_id, application_id, notice_kind,
                 transfer_effective, transferor_label, transferee_label,
                 send_by, status, actor, created_utc)
            VALUES (@noticeId, @appId, @noticeKind,
                    @transferEffective, @transferorLabel, @transfereeLabel,
                    @sendBy, 'pending', @actor, @created)
            """,
            new
            {
                noticeId,
                appId,
                noticeKind,
                transferEffective,
                transferorLabel,
                transfereeLabel,
                sendBy,
                actor,
                created
            }, ct);
        await InsertEventAsync(noticeId, "pending", actor, ct);
        return new ServicingNoticeRecord(noticeId, appId, noticeKind,
            transferEffective, transferorLabel, transfereeLabel,
            sendBy, "pending", actor, created);
    }

    private const string NoticeColumns = """
        notice_id AS NoticeId, application_id AS ApplicationId,
        notice_kind AS NoticeKind, transfer_effective AS TransferEffective,
        transferor_label AS TransferorLabel,
        transferee_label AS TransfereeLabel,
        send_by AS SendBy, status AS Status,
        actor AS Actor, created_utc AS CreatedUtc
        """;

    public async Task<ServicingNoticeRecord?> GetNoticeAsync(
        string noticeId, CancellationToken ct)
        => await QuerySingleOrDefaultAsync<ServicingNoticeRecord>(
            $"""
            SELECT {NoticeColumns} FROM servicing_notices
            WHERE notice_id=@noticeId
            """, new { noticeId }, ct);

    public async Task<IReadOnlyList<ServicingNoticeRecord>>
        ListNoticesAsync(string appId, CancellationToken ct)
        => await QueryAsync<ServicingNoticeRecord>(
            $"""
            SELECT {NoticeColumns} FROM servicing_notices
            WHERE application_id=@appId
            ORDER BY created_utc ASC, rowid ASC
            """, new { appId }, ct);

    public async Task<string> TransitionNoticeAsync(string noticeId,
        string status, string actor, CancellationToken ct)
    {
        await ExecuteAsync(
            """
            UPDATE servicing_notices SET status=@status
            WHERE notice_id=@noticeId
            """, new { noticeId, status }, ct);
        return await InsertEventAsync(noticeId, status, actor, ct);
    }

    private async Task<string> InsertEventAsync(string noticeId,
        string status, string actor, CancellationToken ct)
    {
        var eventId = $"nev-{Guid.NewGuid():N}";
        var created = Now();
        await ExecuteAsync(
            """
            INSERT INTO servicing_notice_events
                (event_id, notice_id, status, actor, created_utc)
            VALUES (@eventId, @noticeId, @status, @actor, @created)
            """, new { eventId, noticeId, status, actor, created }, ct);
        return created;
    }

    public async Task<IReadOnlyList<ServicingNoticeEventRecord>>
        ListNoticeEventsAsync(string noticeId, CancellationToken ct)
        => await QueryAsync<ServicingNoticeEventRecord>(
            """
            SELECT event_id AS EventId, notice_id AS NoticeId,
                   status AS Status, actor AS Actor,
                   created_utc AS CreatedUtc
            FROM servicing_notice_events
            WHERE notice_id=@noticeId
            ORDER BY created_utc ASC, rowid ASC
            """, new { noticeId }, ct);
}
