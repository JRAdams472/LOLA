using System.Security.Cryptography;
using System.Text;

namespace Lola.Application.Audit;

/// Hash-chain mechanics shared by the tenant-file and control-plane
/// audit logs (invariant 4). Each row's hash covers the previous row's
/// hash plus this row's canonical content — modifying or deleting any
/// row breaks every later link.
public static class HashChain
{
    public const string Genesis = "GENESIS";

    /// Canonical row content the hash covers — pipe-separated, same
    /// field order on write and verify.
    public static string ComputeHash(string prevHash, string eventId,
        string actor, string action, string entity, string? entityId,
        IReadOnlyList<string> fieldClasses, string? reason, string createdUtc)
    {
        var canonical = string.Join('|',
            prevHash, eventId, actor, action, entity, entityId ?? "",
            string.Join(',', fieldClasses), reason ?? "", createdUtc);
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(canonical))).ToLowerInvariant();
    }

    /// Walk the chain in seq order — returns the first seq that fails
    /// (stored hash mismatch OR broken prev link) or null when clean.
    public static long? FirstBroken(IReadOnlyList<AuditEvent> events)
    {
        var prev = Genesis;
        foreach (var e in events)
        {
            var expected = ComputeHash(prev, e.EventId, e.Actor, e.Action,
                e.Entity, e.EntityId, e.FieldClasses, e.Reason, e.CreatedUtc);
            if (e.PrevHash != prev || e.Hash != expected) return e.Seq;
            prev = e.Hash;
        }
        return null;
    }
}
