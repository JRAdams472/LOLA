using System.Text.Json;
using Lola.Application.Fees;

namespace Lola.Infrastructure.Fees;

/// Bearer-token source for the Zara engine (MPE-183). Two modes:
/// `ZARA_TOKEN` — a static env-injected JWT — or `ZARA_TOKEN_FILE`,
/// which is read lazily and re-read when the cached token's `exp`
/// claim is within the refresh skew. The file mode is the dev/CI
/// path (zara_devtoken mints into `.devkeys/`); prod wiring is the
/// same file contract fed by a secret manager or token sidecar.
/// `Invalidate` drops the cache so a 401 retry re-reads immediately.
internal sealed class ZaraTokenProvider(ZaraFeeOptions options)
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
        if (options.Token is { } env && !string.IsNullOrWhiteSpace(env))
            return env;                     // env token — no caching,
                                            // the deploy owns rotation
        if (_token is not null && DateTimeOffset.UtcNow < _expiresUtc)
            return _token;
        await _gate.WaitAsync(ct);
        try
        {
            if (_token is not null && DateTimeOffset.UtcNow < _expiresUtc)
                return _token;
            if (options.TokenFile is null)
                throw new FeeAdapterException(FeeErrorKind.Auth,
                    "no Zara token source — set ZARA_TOKEN or " +
                    "ZARA_TOKEN_FILE");
            string raw;
            try
            {
                raw = (await File.ReadAllTextAsync(
                    options.TokenFile, ct)).Trim();
            }
            catch (IOException ex)
            {
                throw new FeeAdapterException(FeeErrorKind.Auth,
                    $"Zara token file unreadable: {ex.Message}", ex);
            }
            if (raw.Length == 0)
                throw new FeeAdapterException(FeeErrorKind.Auth,
                    "Zara token file is empty");
            _expiresUtc = ReadExpiry(raw) - Skew;
            _token = raw;
            return _token;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// `exp` claim from the JWT payload — decoded, not validated
    /// (the engine validates; we only need the refresh boundary).
    /// An unparseable token is treated as expired-now, so the file
    /// is re-read on every call until a valid JWT lands.
    private static DateTimeOffset ReadExpiry(string jwt)
    {
        try
        {
            var parts = jwt.Split('.');
            if (parts.Length != 3) return DateTimeOffset.UtcNow;
            var payload = parts[1]
                .Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight(
                payload.Length + (4 - payload.Length % 4) % 4, '=');
            var claims = JsonDocument.Parse(
                Convert.FromBase64String(payload)).RootElement;
            if (claims.TryGetProperty("exp", out var exp) &&
                exp.TryGetInt64(out var seconds))
                return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception ex) when (ex is FormatException or JsonException)
        {
        }
        return DateTimeOffset.UtcNow;
    }
}
