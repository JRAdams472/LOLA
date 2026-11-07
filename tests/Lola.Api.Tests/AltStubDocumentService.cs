using Lola.Application.Documents;

namespace Lola.Api.Tests;

/// The second IDocumentService implementation (MPE-190) � the
/// endpoint e2e runs on it, proving the swap is interface-level:
/// a different adapter slotting in changes nothing upstream.
/// Completed immediately like the stub, but tagged `alt-stub` and
/// carrying a distinguishable download URL shape.
public sealed class AltStubDocumentService : IDocumentService
{
    /// Captured requests — tests assert exactly what crossed the
    /// adapter boundary (template ids, doc counts, payload shape).
    public System.Collections.Concurrent.ConcurrentQueue<DocPackageRequest>
        Requests
    { get; } = new();

    public string Adapter => "alt-stub";

    public Task<DocGeneration> GenerateAsync(DocPackageRequest request,
        CancellationToken ct = default)
    {
        Requests.Enqueue(request);
        return Task.FromResult(new DocGeneration(
            $"alt-{request.IdempotencyKey}", "completed",
            $"{request.PackageName}_alt.zip"));
    }

    public Task<DocGeneration> GetStatusAsync(string requestId,
        CancellationToken ct = default)
        => Task.FromResult(new DocGeneration(
            requestId, "completed", $"{requestId}.zip"));

    public Task<string> GetDownloadUrlAsync(string requestId,
        CancellationToken ct = default)
        => Task.FromResult(
            $"https://alt-stub.local/dl/{requestId}?sig=test");
}
