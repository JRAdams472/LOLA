using Lola.Application.Vault;

namespace Lola.Infrastructure.Vault;

/// No-op classifier (M18) — echoes the uploader-declared kind at
/// middling confidence with no fields or findings. Proves the
/// `DOC_CLASSIFIER` env swap and gives deployments a way to run the
/// pipeline with intelligence inert.
public sealed class StubDocClassifier : IDocClassifier
{
    public string Adapter => "stub";

    public Task<DocClassification> ClassifyAsync(VaultDocRecord doc,
        ReadOnlyMemory<byte> head, CancellationToken ct = default)
        => Task.FromResult(new DocClassification(
            DocKind.Normalize(doc.Classification)
                is { Length: > 0 } declared
                && DocKind.All.Contains(declared)
                    ? declared : DocKind.Misc,
            0.5,
            new Dictionary<string, string>(),
            new List<DocFinding>()));
}
