using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Lola.Application.Documents;

/// Shared wire shapes for the document surface (MPE-190): the outbox
/// payload and the idempotency hash — one canonical serializer so
/// handler, worker, and adapter always agree.
public static class DocWire
{
    /// camelCase + null-omitted — matches ISPPr's serde rename_all.
    public static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    /// Outbox payload — the full generation request captured at
    /// enqueue time. The worker replays this verbatim; the
    /// application is never re-read, so a later draft can't mutate an
    /// in-flight package.
    public static string PayloadJson(DocPackageRequest request)
        => JsonSerializer.Serialize(request, Json);

    public static DocPackageRequest? ParsePayload(string payloadJson)
        => JsonSerializer.Deserialize<DocPackageRequest>(payloadJson, Json);

    /// Idempotency key: the app + kind plus the canonical request.
    /// Same app asking for the same package twice is a no-op by
    /// definition — the original doc id comes back.
    public static string RequestHash(string appId, string kind,
        string payloadJson)
    {
        var bytes = SHA256.HashData(
            Encoding.UTF8.GetBytes($"{appId}|{kind}|{payloadJson}"));
        return Convert.ToHexStringLower(bytes);
    }

    public static DocDto ToDto(DocRecord r) => new(
        r.DocId, r.ApplicationId, r.Kind, r.PackageName, r.Status,
        r.Filename, r.Error, r.CreatedUtc, r.CompletedUtc);
}
