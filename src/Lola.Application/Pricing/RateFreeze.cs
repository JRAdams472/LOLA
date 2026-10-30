using Lola.Application.Errors;

namespace Lola.Application.Pricing;

/// Frozen-field enforcement (MPE-131): while a rate lock is `active`,
/// patches to the eval-determining catalog paths are rejected 409
/// with the offending field list; everything else passes through.
public static class RateFreeze
{
    public static async Task EnforceAsync(
        IRateLockRepository locks, string appId,
        IEnumerable<string> patchPaths, CancellationToken ct)
    {
        var active = await locks.GetActiveAsync(appId, ct);
        if (active is null) return;
        var frozen = new HashSet<string>(
            active.FrozenPaths, StringComparer.Ordinal);
        var hits = patchPaths.Where(frozen.Contains).ToList();
        if (hits.Count > 0)
            throw DomainException.Conflict(
                $"fields locked by active rate lock: {string.Join(", ", hits)}",
                new Dictionary<string, object?>
                { ["frozenFields"] = hits });
    }
}
