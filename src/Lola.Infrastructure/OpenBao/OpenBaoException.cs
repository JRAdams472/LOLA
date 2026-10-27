using System.Net;
using System.Text.Json;

namespace Lola.Infrastructure.OpenBao;

/// OpenBao API failure. `errors[]` payloads are flattened into the
/// message — they carry server diagnostics only, never plaintext PII
/// (OpenBao errors echo key paths and policy denials, not inputs).
public sealed class OpenBaoException : Exception
{
    public OpenBaoException(HttpStatusCode status, string message)
        : base($"OpenBao {(int)status}: {message}") => Status = status;

    public HttpStatusCode Status { get; }

    public static OpenBaoException FromResponse(HttpStatusCode status, string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("errors", out var errors) &&
                errors.ValueKind == JsonValueKind.Array)
            {
                var messages = errors.EnumerateArray()
                    .Select(e => e.GetString())
                    .Where(m => !string.IsNullOrWhiteSpace(m));
                return new OpenBaoException(status, string.Join("; ", messages));
            }
        }
        catch (JsonException) { /* fall through to raw body */ }
        return new OpenBaoException(status, body);
    }
}
