using System.Security.Cryptography;
using System.Text;
using Lola.Application.Documents;

namespace Lola.Infrastructure.Documents;

/// Default document provider (MPE-189): deterministic COMPLETED
/// packages so the doc seam, outbox, and registry flow are exercisable
/// without ISPPr. `DOC_ADAPTER=isppr` swaps the real adapter in
/// behind the same interface once MPE-190 lands.
public sealed class StubDocumentService : IDocumentService
{
    public string Adapter => "stub";

    public Task<DocGeneration> GenerateAsync(DocPackageRequest request,
        CancellationToken ct = default)
    {
        // Deterministic request id from the idempotency key — a replay
        // returns the same "request", matching vendor idempotency.
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(
                request.IdempotencyKey)))[..16].ToLowerInvariant();
        var requestId = $"stub-{hash}";
        return Task.FromResult(new DocGeneration(
            requestId, "completed",
            $"{request.PackageName}_{requestId}.zip"));
    }

    public Task<DocGeneration> GetStatusAsync(string requestId,
        CancellationToken ct = default)
        => Task.FromResult(new DocGeneration(
            requestId, "completed", $"package_{requestId}.zip"));

    public Task<string> GetDownloadUrlAsync(string requestId,
        CancellationToken ct = default)
        => Task.FromResult(
            $"https://stub.local/packages/{requestId}.zip" +
            "?presigned=stub");
}
