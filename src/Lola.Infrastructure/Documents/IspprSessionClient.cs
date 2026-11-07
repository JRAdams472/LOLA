using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Nodes;
using Lola.Application.Documents;

namespace Lola.Infrastructure.Documents;

/// ISPPr admin-API client (MPE-190): the session-gated management
/// surface used ONLY by provisioning/erasure — `POST /admin/login`
/// with the operator env credentials yields a bearer session token
/// (12h TTL, cached with skew). Client/customer/api-key/template
/// ensure methods are idempotent so a retried provisioning run
/// converges instead of duplicating vendor-side rows.
///
/// All calls are serialized through <see cref="DocAdapterException"/>
/// so provisioning failures surface typed, like adapter failures.
public sealed class IspprSessionClient(HttpClient http,
    IspprAdminOptions options) : IDocVendorAdmin
{
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresUtc = DateTimeOffset.MinValue;

    public async Task<string> EnsureClientAsync(string name,
        CancellationToken ct = default)
    {
        var rows = await GetAsync($"/api/v1/admin/clients", ct);
        var existing = rows?.AsArray()
            .FirstOrDefault(r => r?["name"]?.GetValue<string>() == name);
        if (existing?["id"]?.GetValue<string>() is { } found)
            return found;
        var created = await PostAsync("/api/v1/admin/clients",
            new JsonObject { ["name"] = name, ["status"] = "active" },
            ct);
        return created?["id"]?.GetValue<string>()
            ?? throw DocAdapterException.Contract(
                "ISPPr client create returned no id");
    }

    public async Task<string> EnsureCustomerAsync(string clientId,
        string name, string externalRef, CancellationToken ct = default)
    {
        var rows = await GetAsync(
            $"/api/v1/admin/clients/{clientId}/customers", ct);
        var existing = rows?.AsArray().FirstOrDefault(r =>
            r?["external_ref"]?.GetValue<string>() == externalRef);
        if (existing?["id"]?.GetValue<string>() is { } found)
            return found;
        var created = await PostAsync(
            $"/api/v1/admin/clients/{clientId}/customers",
            new JsonObject
            {
                ["name"] = name,
                ["external_ref"] = externalRef
            }, ct);
        return created?["id"]?.GetValue<string>()
            ?? throw DocAdapterException.Contract(
                "ISPPr customer create returned no id");
    }

    public async Task<(string KeyId, string PlaintextKey)>
        CreateApiKeyAsync(string clientId, string label,
            IReadOnlyList<string> scopes, CancellationToken ct = default)
    {
        var body = new JsonObject
        {
            ["label"] = label,
            ["scopes"] = new JsonArray(
                scopes.Select(s => (JsonNode)s).ToArray())
        };
        var created = await PostAsync(
            $"/api/v1/admin/clients/{clientId}/api-keys", body, ct);
        var id = created?["id"]?.GetValue<string>();
        var key = created?["key"]?.GetValue<string>();
        return id is null || key is null
            ? throw DocAdapterException.Contract(
                "ISPPr key create returned no id/key")
            : (id, key);
    }

    /// 409 = the master already exists — provisioning is re-entrant,
    /// so a re-run uploads nothing twice.
    public async Task<bool> UploadTemplateAsync(string clientId,
        string name, string html, CancellationToken ct = default)
    {
        try
        {
            await PostAsync(
                $"/api/v1/admin/clients/{clientId}/template-masters",
                new JsonObject { ["name"] = name, ["html"] = html }, ct);
            return true;
        }
        catch (DocAdapterException ex) when (ex.Kind ==
            DocErrorKind.Rejected)
        {
            return false;
        }
    }

    public async Task RevokeApiKeyAsync(string keyId,
        CancellationToken ct = default)
        => await PostAsync($"/api/v1/admin/api-keys/{keyId}/revoke",
            null, ct);

    // ---- session plumbing ---------------------------------------------

    private async Task<string> TokenAsync(CancellationToken ct,
        bool force = false)
    {
        if (!force && _token is not null
            && DateTimeOffset.UtcNow < _expiresUtc)
            return _token;
        await _gate.WaitAsync(ct);
        try
        {
            if (!force && _token is not null
                && DateTimeOffset.UtcNow < _expiresUtc)
                return _token;
            var body = new JsonObject
            {
                ["email"] = options.AdminEmail,
                ["password"] = options.AdminPassword
            };
            using var message = new HttpRequestMessage(HttpMethod.Post,
                $"{options.BaseUrl.TrimEnd('/')}/api/v1/admin/login")
            {
                Content = new StringContent(body.ToJsonString(),
                    System.Text.Encoding.UTF8, "application/json")
            };
            var response = await http.SendAsync(message, ct);
            if (!response.IsSuccessStatusCode)
                throw new DocAdapterException(
                    DocErrorKind.Auth,
                    $"ISPPr admin login → {(int)response.StatusCode}",
                    false);
            var node = await response.Content
                .ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
            _token = node?["token"]?.GetValue<string>()
                ?? throw DocAdapterException.Contract(
                    "ISPPr login returned no token");
            _expiresUtc = DateTimeOffset.TryParse(
                node?["expires_at"]?.GetValue<string>(), out var exp)
                ? exp - Skew
                : DateTimeOffset.UtcNow.AddHours(11);
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<JsonNode?> GetAsync(string path,
        CancellationToken ct)
        => await SendAsync(HttpMethod.Get, path, null, ct);

    private async Task<JsonNode?> PostAsync(string path,
        JsonObject? body, CancellationToken ct)
        => await SendAsync(HttpMethod.Post, path, body, ct);

    private async Task<JsonNode?> SendAsync(HttpMethod method,
        string path, JsonObject? body, CancellationToken ct,
        bool retried = false)
    {
        var token = await TokenAsync(ct);
        using var message = new HttpRequestMessage(method,
            $"{options.BaseUrl.TrimEnd('/')}{path}");
        message.Headers.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
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
            // One transport retry — a cold-start connection reset
            // shouldn't fail a whole tenant provisioning run, and the
            // ensure-methods converge on replay.
            if (!retried)
                return await SendAsync(method, path, body, ct,
                    retried: true);
            throw DocAdapterException.Unavailable(
                $"ISPPr admin unreachable: {ex.Message}");
        }
        if (response.StatusCode == HttpStatusCode.Unauthorized
            && !retried)
        {
            // Session expired mid-flight — re-login once.
            _token = null;
            await TokenAsync(ct, force: true);
            return await SendAsync(method, path, body, ct,
                retried: true);
        }
        if (response.StatusCode == HttpStatusCode.Conflict)
            throw DocAdapterException.Rejected(
                $"ISPPr {path} → 409 conflict");
        if (!response.IsSuccessStatusCode)
            throw new DocAdapterException(
                (int)response.StatusCode switch
                {
                    401 or 403 => DocErrorKind.Auth,
                    >= 500 => DocErrorKind.Unavailable,
                    _ => DocErrorKind.Rejected
                },
                $"ISPPr admin {method.Method} {path} → " +
                $"{(int)response.StatusCode}",
                (int)response.StatusCode >= 500);
        try
        {
            return await response.Content
                .ReadFromJsonAsync<JsonNode>(cancellationToken: ct);
        }
        catch (System.Text.Json.JsonException ex)
        {
            throw DocAdapterException.Contract(
                $"ISPPr admin {path} returned unparseable JSON");
        }
    }
}

/// ISPPr admin configuration (MPE-190): `ISPPR_BASE_URL` +
/// `ISPPR_ADMIN_EMAIL`/`ISPPR_ADMIN_PASSWORD` operator env creds.
/// M2M admin auth is deferred ISPPr-side work — the session login is
/// the only admin surface today.
public sealed record IspprAdminOptions(
    string BaseUrl, string AdminEmail, string AdminPassword);
