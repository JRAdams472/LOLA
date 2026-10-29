using System.Net.Http.Json;
using System.Text.Json;
using Lola.Application.Pricing;

namespace Lola.Infrastructure.Pricing;

/// OIDC client-credentials tokens for MPEx (Dex) with an
/// expiry-offset cache (MPE-130). Single-flight via the gate — N
/// concurrent evals share one grant. `Invalidate` drops the cached
/// token so a 401 retry mints a fresh one.
internal sealed class DexTokenProvider(HttpClient http, MpexPricingOptions options)
{
    // Refresh 30s early — a token expiring mid-flight looks like an
    // auth bug, not a clock edge.
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(30);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _token;
    private DateTimeOffset _expiresUtc = DateTimeOffset.MinValue;

    public void Invalidate() => _token = null;

    public async Task<string> GetAsync(CancellationToken ct)
    {
        if (_token is not null && DateTimeOffset.UtcNow < _expiresUtc)
            return _token;
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresUtc)
                return _token;
            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = options.ClientId,
                ["client_secret"] = options.ClientSecret
            };
            if (!string.IsNullOrWhiteSpace(options.Audience))
                form["audience"] = options.Audience;

            using var response = await http.PostAsync(options.TokenUrl,
                new FormUrlEncodedContent(form), ct);
            if (!response.IsSuccessStatusCode)
                throw new PricingAdapterException(PricingErrorKind.Unavailable,
                    $"token grant failed: {(int)response.StatusCode}");

            var json = await response.Content
                .ReadFromJsonAsync<JsonElement>(cancellationToken: ct);
            var token = json.TryGetProperty("access_token", out var at)
                ? at.GetString() : null;
            if (string.IsNullOrEmpty(token))
                throw new PricingAdapterException(PricingErrorKind.Contract,
                    "token response carried no access_token");
            var seconds = json.TryGetProperty("expires_in", out var ei)
                ? ei.GetInt32() : 3600;
            _expiresUtc = DateTimeOffset.UtcNow
                .AddSeconds(seconds) - Skew;
            _token = token;
            return token;
        }
        catch (HttpRequestException ex)
        {
            throw new PricingAdapterException(PricingErrorKind.Unavailable,
                $"token endpoint unreachable: {ex.Message}", ex);
        }
        catch (TaskCanceledException ex) when (ex.InnerException is TimeoutException)
        {
            throw new PricingAdapterException(PricingErrorKind.Unavailable,
                "token endpoint timed out", ex);
        }
        finally
        {
            _gate.Release();
        }
    }
}
