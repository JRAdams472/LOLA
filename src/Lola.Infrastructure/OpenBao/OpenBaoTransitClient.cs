using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Lola.Infrastructure.OpenBao;

/// Thin typed client over the OpenBao transit + sys surface this
/// codebase needs — deliberately small and auditable (ADR-0004).
/// Batch variants use transit `batch_input` so list encrypt/decrypt/
/// rewrap stay a single round-trip. Keys never leave OpenBao; the only
/// secret crossing the wire is the auth token.
public sealed class OpenBaoTransitClient
{
    public const string Mount = "lola-transit";

    private readonly HttpClient _http;
    private readonly OpenBaoOptions _options;

    public OpenBaoTransitClient(HttpClient http, IOptions<OpenBaoOptions> options)
    {
        _http = http;
        _options = options.Value;
    }

    public bool Configured => _options.Configured;

    // --- transit crypto -------------------------------------------------

    /// Encrypts UTF-8 plaintext → `vault:vN:` ciphertext.
    public async Task<string> EncryptAsync(string key, string plaintext, CancellationToken ct = default)
    {
        var data = await PostDataAsync($"{Mount}/encrypt/{key}",
            new { plaintext = ToB64(plaintext) }, ct);
        return data.GetProperty("ciphertext").GetString()!;
    }

    public async Task<IReadOnlyList<string>> EncryptAsync(string key, IReadOnlyList<string> plaintexts, CancellationToken ct = default)
    {
        var batch = plaintexts.Select(p => new { plaintext = ToB64(p) }).ToArray();
        var data = await PostDataAsync($"{Mount}/encrypt/{key}", new { batch_input = batch }, ct);
        return BatchValues(data, "ciphertext");
    }

    /// Decrypts `vault:vN:` ciphertext → UTF-8 plaintext. Throws
    /// <see cref="OpenBaoException"/> when the key/version is gone.
    public async Task<string> DecryptAsync(string key, string ciphertext, CancellationToken ct = default)
    {
        var data = await PostDataAsync($"{Mount}/decrypt/{key}",
            new { ciphertext }, ct);
        return FromB64(data.GetProperty("plaintext").GetString()!);
    }

    public async Task<IReadOnlyList<string>> DecryptAsync(string key, IReadOnlyList<string> ciphertexts, CancellationToken ct = default)
    {
        var batch = ciphertexts.Select(c => new { ciphertext = c }).ToArray();
        var data = await PostDataAsync($"{Mount}/decrypt/{key}", new { batch_input = batch }, ct);
        return BatchValues(data, "plaintext").Select(FromB64).ToList();
    }

    /// Rewraps ciphertext to the key's latest version — inside OpenBao,
    /// so plaintext never crosses the wire during rotation.
    public async Task<string> RewrapAsync(string key, string ciphertext, CancellationToken ct = default)
    {
        var data = await PostDataAsync($"{Mount}/rewrap/{key}",
            new { ciphertext }, ct);
        return data.GetProperty("ciphertext").GetString()!;
    }

    public async Task<IReadOnlyList<string>> RewrapAsync(string key, IReadOnlyList<string> ciphertexts, CancellationToken ct = default)
    {
        var batch = ciphertexts.Select(c => new { ciphertext = c }).ToArray();
        var data = await PostDataAsync($"{Mount}/rewrap/{key}", new { batch_input = batch }, ct);
        return BatchValues(data, "ciphertext");
    }

    /// HMAC over (already-normalized) input — `vault:vN:<hmac>` token,
    /// deterministic under a key version.
    public async Task<string> HmacAsync(string key, string input, CancellationToken ct = default)
    {
        var data = await PostDataAsync($"{Mount}/hmac/{key}",
            new { input = ToB64(input) }, ct);
        return data.GetProperty("hmac").GetString()!;
    }

    // --- key management -------------------------------------------------

    /// Create a transit key if absent — GET first so re-runs don't
    /// attempt a type/config change on an existing key.
    /// `keySize` is required by some types (e.g. hmac wants 32–512
    /// bytes); null omits the field for types that ignore it.
    public async Task EnsureKeyAsync(string keyName, string keyType = "aes256-gcm96",
        int? keySize = null, CancellationToken ct = default)
    {
        using var found = await SendOnceOrRetryAsync(
            () => CreateRequest(HttpMethod.Get, $"{Mount}/keys/{keyName}"), ct);
        if (found.IsSuccessStatusCode) return;

        await PostVoidAsync($"{Mount}/keys/{keyName}",
            keySize is null ? new { type = keyType } : new { type = keyType, key_size = keySize }, ct);
    }

    public async Task RotateKeyAsync(string keyName, CancellationToken ct = default)
    {
        await PostVoidAsync($"{Mount}/keys/{keyName}/rotate", null, ct);
    }

    /// Step 1 of the two-step crypto-shred — transit refuses key
    /// deletion until this flag is set. Deliberate, not convenience.
    public async Task SetDeletionAllowedAsync(string keyName, CancellationToken ct = default)
    {
        await PostVoidAsync($"{Mount}/keys/{keyName}/config", new { deletion_allowed = true }, ct);
    }

    /// Step 2 — irreversible. After this every ciphertext under the key
    /// is permanently unrecoverable.
    public async Task DeleteKeyAsync(string keyName, CancellationToken ct = default)
    {
        using var resp = await SendOnceOrRetryAsync(() => CreateRequest(HttpMethod.Delete, $"{Mount}/keys/{keyName}"), ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw OpenBaoException.FromResponse(resp.StatusCode, body);
    }

    // --- mount + status -------------------------------------------------

    /// Ensure the transit mount exists — root/admin-token operation used
    /// by dev init and the test fixture; the scoped api token does NOT
    /// get sys perms (least privilege).
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
        await PostVoidAsync($"sys/mounts/{Mount}", new { type = "transit" }, ct);
    }

    /// "unsealed" | "sealed" | "unreachable" | "unconfigured" — for
    /// health reporting; throws nothing by design.
    public async Task<string> SealProbeAsync(CancellationToken ct = default)
    {
        if (!Configured) return "unconfigured";
        try
        {
            using var req = CreateRequest(HttpMethod.Get, "sys/seal-status");
            using var resp = await _http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode) return "unreachable";
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            return doc.RootElement.GetProperty("sealed").GetBoolean() ? "sealed" : "unsealed";
        }
        catch (HttpRequestException) { return "unreachable"; }
        catch (TaskCanceledException) { return "unreachable"; }
    }

    // --- plumbing -------------------------------------------------------

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

    /// Send with a single retry on stale-connection failures. A request
    /// that throws during SendAsync never reached the server (dead
    /// keep-alive socket discovered on write) — retrying is safe for
    /// every op here, including nominally non-idempotent ones, since no
    /// bytes were written. Requests are rebuilt via the factory because
    /// HttpRequestMessage is single-use.
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

    private async Task<JsonElement> PostDataAsync(string path, object body, CancellationToken ct)
    {
        using var resp = await SendOnceOrRetryAsync(() =>
        {
            var req = CreateRequest(HttpMethod.Post, path);
            req.Content = JsonContent.Create(body);
            return req;
        }, ct);
        var text = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw OpenBaoException.FromResponse(resp.StatusCode, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private async Task PostVoidAsync(string path, object? body, CancellationToken ct)
    {
        using var resp = await SendOnceOrRetryAsync(() =>
        {
            var req = CreateRequest(HttpMethod.Post, path);
            if (body is not null) req.Content = JsonContent.Create(body);
            return req;
        }, ct);
        if (!resp.IsSuccessStatusCode)
        {
            var text = await resp.Content.ReadAsStringAsync(ct);
            throw OpenBaoException.FromResponse(resp.StatusCode, text);
        }
    }

    private static List<string> BatchValues(JsonElement data, string field) =>
        data.GetProperty("batch_results").EnumerateArray()
            .Select(r => r.GetProperty(field).GetString()!)
            .ToList();

    private static string ToB64(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string FromB64(string value) =>
        Encoding.UTF8.GetString(Convert.FromBase64String(value));
}
