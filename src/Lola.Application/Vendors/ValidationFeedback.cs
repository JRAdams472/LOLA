namespace Lola.Application.Vendors;

/// Finding kinds a validation source can report back (M17). `cleared`
/// resolves a linked open condition; `data_error`/`missing_field`/
/// `exception` open new conditions — `exception` additionally lands a
/// `validation_exception` task on the M16 queue.
public static class FindingKind
{
    public const string DataError = "data_error";
    public const string MissingField = "missing_field";
    public const string Exception = "exception";
    public const string Cleared = "cleared";

    public static readonly IReadOnlySet<string> All = new HashSet<string>
        { DataError, MissingField, Exception, Cleared };
}

/// Source-reported severity. `error` maps to High task priority on the
/// queue; `info`/`warning` stay condition-level.
public static class FeedbackSeverity
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";

    public static readonly IReadOnlySet<string> All =
        new HashSet<string> { Info, Warning, Error };
}

/// `conditions.linked_entity` value stamped on feedback-raised
/// conditions (M17) — the marker the task evaluator reads to emit
/// `validation_exception` tasks instead of plain `condition_blocking`,
/// and the link back to the `vendor_feedback` ledger row.
public static class FeedbackLink
{
    public const string VendorFeedback = "vendor_feedback";
}

/// Normalized validation feedback pushed by a signed source webhook
/// (M17, ADR-0007) — the payload-minimal contract the PII firewall
/// enforces member-for-member (invariant 2). Every field is a
/// reference, an enum token, or an opaque source id: the source
/// already holds the data it reports on, so nothing PII-class ever
/// needs to ride this envelope.
public sealed record ValidationFeedback(
    string Source,            // mpex|isppr — the webhook path segment
    string ApplicationId,
    string FeedbackId,        // source-unique; the replay dedupe key
    string Finding,           // FindingKind.*
    string Severity,          // FeedbackSeverity.*
    string? ConditionId,      // condition a `cleared` finding resolves
    string? LinkedEntity,     // entity a new condition binds to
    string? LinkedEntityId);
