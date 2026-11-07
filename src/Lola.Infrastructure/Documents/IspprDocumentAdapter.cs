using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Lola.Application.Documents;

namespace Lola.Infrastructure.Documents;

/// ISPPr adapter (MPE-190, ADR-0007 amended): the tenant generation
/// surface behind X-API-Key — `POST /api/v1/packages/generate`,
/// `GET /api/v1/packages/{id}`, `GET /{id}/download`. The API key is
/// resolved per call from <see cref="IDocumentCredentialSource"/>
/// (the current tenant's wrapped key, unwrapped in-memory for this
/// call only — never stored, never logged).
///
/// Wire shape (docs/tenant-api.md):
///   POST /generate {customer_id, package_name,
///                   documents:[{template_id, payload, print_ready?}]}
///   → 201 {request_id, status:"COMPLETED",
///          results:{filename, download_url}}
///   → 202 {request_id, status:"QUEUED"|"RENDERING"|"COMPRESSING"}
///   GET /{id} → same shape; FAILED carries `error`
///   GET /{id}/download → 302 Location = fresh presigned URL (s3)
///
/// Statuses normalize to the seam's lowercase vocabulary:
/// COMPLETED→completed, FAILED→failed, anything else→generating.
public sealed class IspprDocumentAdapter(HttpClient http,
    IspprDocOptions options, IDocumentCredentialSource credentials)
    : IDocumentService
{
    public string Adapter => "isppr";

    public async Task<DocGeneration> GenerateAsync(
        DocPackageRequest request, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["customer_id"] = request.CustomerId,
            ["package_name"] = request.PackageName,
            ["documents"] = new JsonArray(request.Documents.Select(d =>
                (JsonNode)new JsonObject
                {
                    ["template_id"] = d.TemplateId,
                    ["payload"] = d.Payload.DeepClone(),
                    ["print_ready"] = d.PrintReady
                }).ToArray())
        };
        var node = await SendAsync(HttpMethod.Post,
            "/api/v1/packages/generate", body,
            request.IdempotencyKey, ct);
        return ParseGeneration(node);
    }

    public async Task<DocGeneration> GetStatusAsync(string requestId,
        CancellationToken ct = default)
        => ParseGeneration(await SendAsync(HttpMethod.Get,
            $"/api/v1/packages/{requestId}", null, null, ct));

    /// The download route 302s to a fresh presigned URL — the client
    /// is registered with AllowAutoRedirect=false so the Location
    /// surfaces instead of being followed. Under a local storage
    /// backend the route streams bytes; the route URL itself is then
    /// the download location.
    public async Task<string> GetDownloadUrlAsync(string requestId,
        CancellationToken ct = default)
    {
        var key = await ApiKeyAsync(ct);
        var url = $"{options.BaseUrl.TrimEnd('/')}" +
            $"/api/v1/packages/{requestId}/download";
        using var message = new HttpRequestMessage(HttpMethod.Get, url);
        message.Headers.Add("X-API-Key", key);
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (HttpRequestException ex)
        {
            throw DocAdapterException.Unavailable(
                $"ISPPr unreachable: {ex.Message}");
        }
        catch (TaskCanceledException ex)
            when (ex.InnerException is TimeoutException)
        {
            throw DocAdapterException.Unavailable("ISPPr timed out");
        }
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            var location = response.Headers.Location
                ?? throw DocAdapterException.Contract(
                    "ISPPr download redirect carried no Location");
            return location.IsAbsoluteUri
                ? location.ToString()
                : new Uri(new Uri(options.BaseUrl.TrimEnd('/') + '/'),
                    location).ToString();
        }
        if (response.StatusCode == HttpStatusCode.OK)
            return url;
        throw new DocAdapterException(Classify(response.StatusCode),
            $"ISPPr download → {(int)response.StatusCode}",
            response.StatusCode == HttpStatusCode.TooManyRequests
            || (int)response.StatusCode >= 500);
    }

    private async Task<string> ApiKeyAsync(CancellationToken ct)
        => await credentials.ApiKeyAsync(ct)
            ?? throw DocAdapterException.Auth(
                "tenant has no ISPPr key — provisioning incomplete");

    private async Task<JsonNode?> SendAsync(HttpMethod method,
        string path, JsonObject? body, string? idempotencyKey,
        CancellationToken ct)
    {
        var key = await ApiKeyAsync(ct);
        using var message = new HttpRequestMessage(method,
            $"{options.BaseUrl.TrimEnd('/')}{path}");
        message.Headers.Add("X-API-Key", key);
        if (idempotencyKey is not null)
            message.Headers.Add("Idempotency-Key", idempotencyKey);
        if (body is not null)
            message.Content = new StringContent(body.ToJsonString(),
                System.Text.Encoding.UTF8, "application/json");
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(message, ct);
        }
        catch (HttpRequestException ex)
        {
            throw DocAdapterException.Unavailable(
                $"ISPPr unreachable: {ex.Message}");
        }
        catch (TaskCanceledException ex)
            when (ex.InnerException is TimeoutException)
        {
            throw DocAdapterException.Unavailable("ISPPr timed out");
        }

        if (!response.IsSuccessStatusCode)
            throw new DocAdapterException(Classify(response.StatusCode),
                $"ISPPr {method.Method} {path} → " +
                $"{(int)response.StatusCode}",
                response.StatusCode == HttpStatusCode.TooManyRequests
                || (int)response.StatusCode >= 500);
        try
        {
            return await response.Content
                .ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw DocAdapterException.Contract(
                $"ISPPr {path} returned unparseable JSON");
        }
    }

    private static DocGeneration ParseGeneration(JsonNode? node)
    {
        var requestId = node?["request_id"]?.GetValue<string>()
            ?? throw DocAdapterException.Contract(
                "ISPPr response carried no request_id");
        var status = node?["status"]?.GetValue<string>() switch
        {
            "COMPLETED" => "completed",
            "FAILED" => "failed",
            _ => "generating"
        };
        var filename = node?["results"]?["filename"]?.GetValue<string>();
        var error = node?["error"]?.GetValue<string>();
        return new DocGeneration(requestId, status, filename, error);
    }

    private static DocErrorKind Classify(HttpStatusCode code)
        => (int)code switch
        {
            401 or 403 => DocErrorKind.Auth,
            400 or 404 or 413 or 422 => DocErrorKind.Rejected,
            >= 500 => DocErrorKind.Unavailable,
            _ => DocErrorKind.Rejected
        };
}

/// ISPPr tenant-API configuration — `ISPPR_BASE_URL`.
public sealed record IspprDocOptions(string BaseUrl);
