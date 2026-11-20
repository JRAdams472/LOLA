using Lola.Application.Vault;

namespace Lola.Infrastructure.Vault;

/// `DOC_CLASSIFIER` selector (M18, ADR-0007) — `rule` (default) is the
/// deterministic table impl; `stub` echoes the declared kind. Unknown
/// names fail fast, never silently pick one.
public static class DocClassifierCatalog
{
    public static IDocClassifier Select(string? name,
        TimeProvider clock)
        => (name ?? "rule").ToLowerInvariant() switch
        {
            "rule" => new RuleDocClassifier(clock),
            "stub" => new StubDocClassifier(),
            var other => throw new InvalidOperationException(
                $"unknown DOC_CLASSIFIER '{other}'"),
        };
}
