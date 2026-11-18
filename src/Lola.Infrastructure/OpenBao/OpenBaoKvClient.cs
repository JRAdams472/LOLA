using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lola.Infrastructure.OpenBao;

/// Thin typed client over the OpenBao KVv2 surface (M13/MPE-77) —
/// vendor credentials live here under `lola-kv/vendors/{tenant}/
/// {kind}`, never in config, DB rows, or the repo. Deliberately small
/// and auditable like <see cref="OpenBaoTransitClient"/>; secret
/// values never appear in exceptions or logs (invariant 3).
public sealed class OpenBaoKvClient
{
    public const string Mount = "lola-kv";

    private readonly HttpClient _http;
    private readonly OpenBaoOptions _options;

    public OpenBaoKvClient(HttpClient http, IOptions<OpenBaoOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public bool Configured => _options.Configured;

    /// Ensure the lola-kv v2 mount exists — root/admin-token operation
    /// used by dev init and the test fixture; the scoped api token does
    /// NOT get sys perms (least privilege, same as the transit mount).
    public async Task EnsureMountAsync(CancellationToken ct = default)
    {
        using var req = CreateRequest(HttpMethod.Get, "sys/mounts");
        using var resp = await _http.SendAsync(req, ct);
        if (resp.IsSuccessStatusCode)
        {
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.TryGetProperty($"{Mount}/", out _)) return;
            if (doc.RootElement.TryGetProperty("data", out var data) &&
                data.TryGetProperty($"{Mount}/", out _)) return;
        }
        using var write = await SendOnceOrRetryAsync(() =>
        {
            var m = CreateRequest(HttpMethod.Post, $"sys/mounts/{Mount}");
            m.Content = JsonContent.Create(
                new { type = "kv", options = new { version = "2" } });
            return m;
        }, ct);
        if (!write.IsSuccessStatusCode)
        {
            var text = await write.Content.ReadAsStringAsync(ct);
            throw OpenBaoException.FromResponse(write.StatusCode, text);
        }
    }

    /// Read a KVv2 secret. Null = absent (404 is a normal miss, not an
    /// error — an unbound tenant kind resolves no credentials).
    public async Task<IReadOnlyDictionary<string, string>?> ReadAsync(
        string path, CancellationToken ct = default)
    {
        using var resp = await SendOnceOrRetryAsync(
            () => CreateRequest(HttpMethod.Get,
                $"{Mount}/data/{path}"), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw OpenBaoException.FromResponse(resp.StatusCode, text);
        using var doc = JsonDocument.Parse(text);
        var data = doc.RootElement
            .GetProperty("data").GetProperty("data");
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var prop in data.EnumerateObject())
            if (prop.Value.ValueKind == JsonValueKind.String)
                map[prop.Name] = prop.Value.GetString()!;
        return map;
    }

    /// Write a KVv2 secret — whole-document replace.
    public async Task WriteAsync(string path,
        IReadOnlyDictionary<string, string> values,
        CancellationToken ct = default)
    {
        using var resp = await SendOnceOrRetryAsync(() =>
        {
            var req = CreateRequest(HttpMethod.Post,
                $"{Mount}/data/{path}");
            req.Content = JsonContent.Create(new { data = values });
            return req;
        }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw OpenBaoException.FromResponse(resp.StatusCode, text);
        }
    }

    /// Delete the latest version — used by tenant erasure so vendor
    /// creds shred with the tenant (invariant 9 adjunct).
    public async Task DeleteAsync(string path, CancellationToken ct = default)
    {
        using var resp = await SendOnceOrRetryAsync(
            () => CreateRequest(HttpMethod.Delete,
                $"{Mount}/data/{path}"), ct);
        if (resp.StatusCode == System.Net.HttpStatusCode.NotFound)
            return;
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw OpenBaoException.FromResponse(resp.StatusCode, text);
        }
    }

    private HttpRequestMessage CreateRequest(HttpMethod method, string path)
    {
        var address = _options.Address
            ?? throw new InvalidOperationException("OPENBAO_ADDR is not configured");
        var req = new HttpRequestMessage(method, $"{address.TrimEnd('/')}/v1/{path}");
        var token = _options.ResolveToken();
        if (!string.IsNullOrEmpty(token))
            req.Headers.TryAddWithoutValidation("X-Vault-Token", token);
        return req;
    }

    /// Same stale-socket retry as the transit client: a send failure
    /// before bytes were written is safe to retry for every op here.
    private async Task<HttpResponseMessage> SendOnceOrRetryAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct)
    {
        using var first = requestFactory();
        try
        {
            return await _http.SendAsync(first, ct);
        }
        catch (HttpRequestException) when (!ct.IsCancellationRequested)
        {
            return await _http.SendAsync(requestFactory(), ct);
        }
    }
}
