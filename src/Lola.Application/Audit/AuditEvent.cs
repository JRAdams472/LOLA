namespace Lola.Application.Audit;

/// One append-only audit event — attributed to an actor, naming the
/// entity and the PII field CLASSES touched (never values, invariant
/// 3). `Hash` chains this row to the previous one for tamper evidence.
public sealed record AuditEvent(
    long Seq, string EventId, string Actor, string Action,
    string Entity, string? EntityId, IReadOnlyList<string> FieldClasses,
    string? Reason, string PrevHash, string Hash, string CreatedUtc);

/// Chain-verification outcome: `Ok` means every row recomputes and
/// links; `FirstBrokenSeq` is the first seq whose stored hash does not
/// recompute (row tampered) or whose prev_hash does not match the
/// predecessor's hash (row deleted/inserted).
public sealed record AuditVerifyResult(bool Ok, long? FirstBrokenSeq, int EventCount);
