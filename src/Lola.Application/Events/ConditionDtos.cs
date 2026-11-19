namespace Lola.Application.Events;

/// Staff-facing condition view — every column, including internal
/// detail and linkage (officer console + GraphQL).
public sealed record ConditionDto(
    string ConditionId, string ApplicationId, string Kind,
    string State, bool Blocking, string? LinkedEntity,
    string? LinkedEntityId, string Title, string? Detail,
    string OpenedBy, string OpenedUtc, string? ClosedUtc,
    string? CloseReason, string? WaiverReason)
{
    public static ConditionDto Of(Condition c) => new(
        c.ConditionId, c.ApplicationId, c.Kind, c.State, c.Blocking,
        c.LinkedEntity, c.LinkedEntityId, c.Title, c.Detail,
        c.OpenedBy, c.OpenedUtc, c.ClosedUtc, c.CloseReason,
        c.WaiverReason);
}

/// Borrower-scoped condition view (M12 projection pattern) — public
/// fields only: no internal detail, linkage, actor, or staff reasons.
public sealed record BorrowerConditionDto(
    string ConditionId, string Kind, string State, bool Blocking,
    string Title, string OpenedUtc, string? ClosedUtc)
{
    public static BorrowerConditionDto Of(Condition c) => new(
        c.ConditionId, c.Kind, c.State, c.Blocking, c.Title,
        c.OpenedUtc, c.ClosedUtc);
}
