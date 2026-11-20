using Lola.Application.Security;
using Lola.Domain.Classification;

namespace Lola.Application.Events;

/// Mutation event kinds written to `loan_events` (M15). Additive log —
/// snapshots remain the source of truth (invariant 6).
public static class LoanEventKind
{
    public const string FieldChange = "field_change";
    public const string Override = "override";
    public const string Escalation = "escalation";
    public const string Transition = "transition";
    public const string Reveal = "reveal";
    public const string ConditionOpened = "condition_opened";
    public const string ConditionClosed = "condition_closed";
    public const string ConditionWaived = "condition_waived";
    public const string ConditionReopened = "condition_reopened";
    public const string ValidationReceived = "validation_received";
}

/// One append-only mutation event. `BeforeVal`/`AfterVal` carry
/// blind-index HMACs for PII-classed fields (invariant 1 — never
/// plaintext), plaintext otherwise. `Hash` chains this row to the
/// previous one (invariant 4).
public sealed record LoanEvent(
    long Seq, string EventId, string ApplicationId, string EventKind,
    string Actor, string Entity, string? EntityId, string? FieldPath,
    string? FieldClass, string? BeforeVal, string? AfterVal,
    string PrevHash, string Hash, string CreatedUtc);

/// What the writer hands the log: raw before/after plus the field's
/// PII classification. `ILoanEventLog` encodes values — PII-classed
/// values are HMAC'd via the tenant blind index, others pass through.
public sealed record LoanEventDraft(
    string ApplicationId, string EventKind, string Actor,
    string Entity, string? EntityId = null, string? FieldPath = null,
    PiiClass? FieldClass = null, string? BeforeVal = null,
    string? AfterVal = null);

/// Chain-verification outcome for `loan_events` — same shape as
/// AuditVerifyResult: clean or the first seq that fails recompute/link.
public sealed record EventVerifyResult(
    bool Ok, long? FirstBrokenSeq, int EventCount);
