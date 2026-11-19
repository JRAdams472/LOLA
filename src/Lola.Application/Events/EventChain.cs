using System.Security.Cryptography;
using System.Text;
using Lola.Application.Audit;

namespace Lola.Application.Events;

/// Hash-chain mechanics for `loan_events` — same protocol as
/// <see cref="Audit.HashChain"/> with the mutation columns included in
/// the canonical string. Modifying or deleting any row breaks every
/// later link.
public static class EventChain
{
    /// Canonical row content the hash covers — pipe-separated, same
    /// field order on write and verify.
    public static string ComputeHash(string prevHash, string eventId,
        string applicationId, string eventKind, string actor,
        string entity, string? entityId, string? fieldPath,
        string? fieldClass, string? beforeVal, string? afterVal,
        string createdUtc)
    {
        var canonical = string.Join('|',
            prevHash, eventId, applicationId, eventKind, actor, entity,
            entityId ?? "", fieldPath ?? "", fieldClass ?? "",
            beforeVal ?? "", afterVal ?? "", createdUtc);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// Walk the chain in seq order — returns the first seq that fails
    /// (stored hash mismatch OR broken prev link) or null when clean.
    public static long? FirstBroken(IReadOnlyList<LoanEvent> events)
    {
        var prev = HashChain.Genesis;
        foreach (var e in events)
        {
            var expected = ComputeHash(prev, e.EventId, e.ApplicationId,
                e.EventKind, e.Actor, e.Entity, e.EntityId, e.FieldPath,
                e.FieldClass, e.BeforeVal, e.AfterVal, e.CreatedUtc);
            if (e.PrevHash != prev || e.Hash != expected) return e.Seq;
            prev = e.Hash;
        }
        return null;
    }
}
