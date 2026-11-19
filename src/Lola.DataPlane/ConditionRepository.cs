using Dapper;
using Lola.Application.Errors;
using Lola.Application.Events;

namespace Lola.DataPlane;

/// Tenant-file conditions store (M15 P1) — tracking items opened by
/// staff or by auto-derivation (`auto:<source>`). State machine:
/// open → closed | waived (terminal); closed → open (reopen).
/// Condition lifecycle writes are `loan_events` events (P3 wires the
/// emission at the handler level).
public sealed class ConditionRepository(TenantConnectionFactory factory)
    : TenantRepository(factory), IConditionRepository
{
    private const string Cols = """
        condition_id AS ConditionId, application_id AS ApplicationId,
        kind AS Kind, state AS State, blocking AS Blocking,
        linked_entity AS LinkedEntity, linked_entity_id AS LinkedEntityId,
        title AS Title, detail AS Detail, opened_by AS OpenedBy,
        opened_utc AS OpenedUtc, closed_utc AS ClosedUtc,
        close_reason AS CloseReason, waiver_reason AS WaiverReason
        """;

    private sealed class Row
    {
        public string ConditionId { get; set; } = "";
        public string ApplicationId { get; set; } = "";
        public string Kind { get; set; } = "";
        public string State { get; set; } = "";
        public long Blocking { get; set; }
        public string? LinkedEntity { get; set; }
        public string? LinkedEntityId { get; set; }
        public string Title { get; set; } = "";
        public string? Detail { get; set; }
        public string OpenedBy { get; set; } = "";
        public string OpenedUtc { get; set; } = "";
        public string? ClosedUtc { get; set; }
        public string? CloseReason { get; set; }
        public string? WaiverReason { get; set; }

        public Condition ToRecord() => new(ConditionId, ApplicationId,
            Kind, State, Blocking != 0, LinkedEntity, LinkedEntityId,
            Title, Detail, OpenedBy, OpenedUtc, ClosedUtc, CloseReason,
            WaiverReason);
    }

    public async Task<Condition> OpenAsync(ConditionDraft draft,
        CancellationToken ct = default)
    {
        if (!ConditionKind.All.Contains(draft.Kind))
            throw DomainException.BadRequest(
                $"unknown condition kind '{draft.Kind}'");
        var id = $"cond-{Guid.NewGuid():N}";
        await ExecuteAsync(
            """
            INSERT INTO conditions
                (condition_id, application_id, kind, state, blocking,
                 linked_entity, linked_entity_id, title, detail, opened_by)
            VALUES (@id, @appId, @kind, 'open', @blocking,
                    @linkedEntity, @linkedEntityId, @title, @detail,
                    @openedBy)
            """,
            new
            {
                id,
                appId = draft.ApplicationId,
                kind = draft.Kind,
                blocking = draft.Blocking ? 1 : 0,
                linkedEntity = draft.LinkedEntity,
                linkedEntityId = draft.LinkedEntityId,
                title = draft.Title,
                detail = draft.Detail,
                openedBy = draft.OpenedBy
            }, ct);
        return (await GetAsync(id, ct))!;
    }

    public async Task<Condition?> GetAsync(string conditionId,
        CancellationToken ct = default)
    {
        var row = await QuerySingleOrDefaultAsync<Row>(
            $"SELECT {Cols} FROM conditions WHERE condition_id = @id",
            new { id = conditionId }, ct);
        return row?.ToRecord();
    }

    public async Task<IReadOnlyList<Condition>> ListByApplicationAsync(
        string applicationId, string? state = null,
        CancellationToken ct = default)
    {
        var rows = await QueryAsync<Row>(
            $"SELECT {Cols} FROM conditions"
            + " WHERE application_id = @appId"
            + " AND (@state IS NULL OR state = @state)"
            + " ORDER BY seq",
            new { appId = applicationId, state }, ct);
        return rows.Select(r => r.ToRecord()).ToList();
    }

    public async Task<Condition> TransitionAsync(string conditionId,
        string toState, string? reason, CancellationToken ct = default)
    {
        var current = await GetAsync(conditionId, ct)
            ?? throw DomainException.NotFound("condition not found");
        var legal = (current.State, toState) switch
        {
            (ConditionState.Open, ConditionState.Closed) => true,
            (ConditionState.Open, ConditionState.Waived) => true,
            (ConditionState.Closed, ConditionState.Open) => true,
            _ => false
        };
        if (!legal)
            throw DomainException.BadRequest(
                $"condition cannot move '{current.State}' → '{toState}'");

        var sql = toState switch
        {
            ConditionState.Closed =>
                "UPDATE conditions SET state='closed',"
                + " closed_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now'),"
                + " close_reason=@reason WHERE condition_id=@id",
            ConditionState.Waived =>
                "UPDATE conditions SET state='waived',"
                + " closed_utc=strftime('%Y-%m-%dT%H:%M:%fZ','now'),"
                + " waiver_reason=@reason WHERE condition_id=@id",
            _ =>
                "UPDATE conditions SET state='open', closed_utc=NULL,"
                + " close_reason=NULL, waiver_reason=NULL"
                + " WHERE condition_id=@id"
        };
        await ExecuteAsync(sql, new { id = conditionId, reason }, ct);
        return (await GetAsync(conditionId, ct))!;
    }
}
