using Lola.Application.Security;
using Lola.Domain.Classification;

namespace Lola.Application.Events;

/// Value encoding for `loan_events` (M15 P1) — the only place
/// before/after strings are produced. PII-classed values become
/// blind-index HMACs (tenant-keyed, deterministic — a repeated value
/// shows as "unchanged" without ever resting plaintext, invariant 1);
/// everything else passes through verbatim.
public static class LoanEventEncoding
{
    public static async Task<LoanEventDraft> EncodeAsync(
        LoanEventDraft draft, IBlindIndex blindIndex,
        CancellationToken ct = default)
    {
        if (draft.FieldClass is not { } cls)
            return draft;
        var before = draft.BeforeVal is null
            ? null : await blindIndex.IndexAsync(cls, draft.BeforeVal, ct);
        var after = draft.AfterVal is null
            ? null : await blindIndex.IndexAsync(cls, draft.AfterVal, ct);
        return draft with { BeforeVal = before, AfterVal = after };
    }
}
