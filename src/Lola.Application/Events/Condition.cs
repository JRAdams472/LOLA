namespace Lola.Application.Events;

/// Condition kinds — the source dimension M17 (feedback) and M18
/// (doc intelligence) write against; `manual` is staff-created.
public static class ConditionKind
{
    public const string DocRequired = "doc_required";
    public const string DataValidation = "data_validation";
    public const string DistanceCheck = "distance_check";
    public const string Manual = "manual";
    public const string ToleranceCure = "tolerance_cure";
    public const string ServicingNotice = "servicing_notice";
    public const string AusFinding = "aus_finding";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { DocRequired, DataValidation, DistanceCheck,
            Manual, ToleranceCure, ServicingNotice, AusFinding };
}

/// `linked_entity` conventions on classifier-written conditions
/// (M18): `doc_kind` marks a requirement seeded by kind ("need a
/// w2") that a clean classified doc satisfies; `vault_doc` marks a
/// replace-doc condition tied to one flagged upload; `application`
/// marks a loan-level review flag (distance_check).
public static class ConditionLink
{
    public const string DocKind = "doc_kind";
    public const string VaultDoc = "vault_doc";
    public const string Application = "application";
    public const string DisclosureVersion = "disclosure_version";
    public const string ServicingNotice = "servicing_notice";
    public const string AusFinding = "aus_finding";
}

/// Condition states: open → closed (reason) | waived (reason + audit,
/// terminal); closed → open (reopen).
public static class ConditionState
{
    public const string Open = "open";
    public const string Closed = "closed";
    public const string Waived = "waived";
}

/// A tracking item on an application — auto-derived (opened_by =
/// `auto:<source>`) or staff-created. Blocking conditions gate
/// submit/downstream steps.
public sealed record Condition(
    string ConditionId, string ApplicationId, string Kind,
    string State, bool Blocking, string? LinkedEntity,
    string? LinkedEntityId, string Title, string? Detail,
    string OpenedBy, string OpenedUtc, string? ClosedUtc,
    string? CloseReason, string? WaiverReason);

/// Fields a caller may supply when opening a condition.
public sealed record ConditionDraft(
    string ApplicationId, string Kind, string Title,
    string? Detail = null, bool Blocking = false,
    string? LinkedEntity = null, string? LinkedEntityId = null,
    string OpenedBy = "system");
