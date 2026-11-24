using System.Security.Cryptography;

namespace Lola.Api.Tests;

/// Disposable production-profile env matrix (MPE-277): sets the full
/// env-var surface the fail-closed validator requires, with disposable
/// PEM key files + temp dirs, and restores every original value on
/// Dispose. The validator reads `builder.Configuration` before
/// ConfigureAppConfiguration merges — so these MUST be real env vars,
/// mirroring the OIDC_*_KEY_PATH trick in LolaApiFactory.
internal sealed class ProdProfileEnv : IDisposable
{
    /// Env keys the matrix owns — restored (or cleared) on Dispose.
    /// Covers the validator's WHOLE surface: required keys are set,
    /// ambient dev/compose values (egress URLs, seed secrets, live-test
    /// creds) are cleared — a leaked http localhost URL or stub affordance
    /// is exactly what the production gate exists to reject.
    private static readonly string[] Keys =
    [
        "CONTROL_DB_KEY", "CONTROL_DB_KEY_FILE",
        "OPENBAO_ADDR", "OPENBAO_TOKEN", "OPENBAO_TOKEN_FILE",
        "OIDC_SIGNING_KEY", "OIDC_SIGNING_KEY_FILE",
        "OIDC_ENCRYPTION_KEY", "OIDC_ENCRYPTION_KEY_FILE",
        "OIDC_ISSUER",
        "OIDC_CONSOLE_CALLBACK_URI", "OIDC_PORTAL_CALLBACK_URI",
        "OIDC_CONSOLE_POST_LOGOUT_URI", "OIDC_PORTAL_POST_LOGOUT_URI",
        "OIDC_SEED_SVC_SECRET", "OIDC_SEED_OPS_SECRET",
        "DP_KEY_DIR", "PORTAL_BASE_URL", "CORS_ORIGIN",
        "TRUSTED_PROXIES", "INSECURE_ENDPOINT_ALLOWLIST",
        "PRICING_ADAPTER", "FEE_ADAPTER", "DOC_ADAPTER",
        "VAULT_ADAPTER", "VENDOR_ADAPTER", "DOC_CLASSIFIER",
        "MPEX_BASE_URL", "MPEX_TOKEN_URL", "MPEX_CLIENT_ID",
        "MPEX_CLIENT_SECRET", "MPEX_AUDIENCE",
        "ZARA_BASE_URL", "ZARA_ENV", "ZARA_TENANT_ID",
        "ZARA_TOKEN", "ZARA_TOKEN_FILE",
        "ISPPR_BASE_URL", "ISPPR_ADMIN_EMAIL", "ISPPR_ADMIN_PASSWORD",
        "ISPPR_API_KEY", "ISPPR_CUSTOMER_ID",
        "S3_ENDPOINT", "S3_PUBLIC_ENDPOINT", "S3_ACCESS_KEY",
        "S3_SECRET_KEY", "CLAMAV_ADDR",
        "VENDOR_STUB_WEBHOOK_URL",
        "DEV_SEED_TENANT", "DEV_SEED_TENANT_SLUG", "DEV_SEED_TENANT_NAME",
        "DEV_SEED_ADMIN_EMAIL", "DEV_SEED_ADMIN_PASSWORD",
        "DEV_SEED_ADMIN_NAME", "DEV_SEED_OFFICER_EMAIL",
        "DEV_SEED_OFFICER_PASSWORD", "DEV_SEED_OFFICER_NAME",
    ];

    private readonly Dictionary<string, string?> _original = new();
    private readonly IDisposable _envWriteLock;
    private readonly string _dir;

    /// Path of the disposable signing PEM the matrix wrote.
    public string SigningKeyFile { get; }
    public string EncryptionKeyFile { get; }

    private ProdProfileEnv()
    {
        // Hold the env write lock for the whole matrix lifetime —
        // every host build (LolaApiFactory.CreateClient/Server/
        // Services) waits on the matching read lock rather than
        // building against the production profile's env vars.
        _envWriteLock = LolaApiFactory.AcquireEnvWriteLock();
        _dir = Path.Combine(Path.GetTempPath(), "lola-prodtest",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Directory.CreateDirectory(Path.Combine(_dir, "dpkeys"));

        SigningKeyFile = Path.Combine(_dir, "signing.pem");
        EncryptionKeyFile = Path.Combine(_dir, "encryption.pem");
        using (var rsa = RSA.Create(2048))
        {
            File.WriteAllText(SigningKeyFile, rsa.ExportPkcs8PrivateKeyPem());
        }
        using (var rsa = RSA.Create(2048))
        {
            File.WriteAllText(EncryptionKeyFile,
                rsa.ExportPkcs8PrivateKeyPem());
        }

        Set("CONTROL_DB_KEY", TestDbKeys.ControlFileKeyHex);
        Set("OPENBAO_ADDR", "https://bao.test");
        Set("OPENBAO_TOKEN", "prod-token-abcdefghij");
        Set("OIDC_SIGNING_KEY_FILE", SigningKeyFile);
        Set("OIDC_ENCRYPTION_KEY_FILE", EncryptionKeyFile);
        Set("OIDC_ISSUER", "https://idp.example.com");
        Set("OIDC_CONSOLE_CALLBACK_URI",
            "https://console.example.com/callback");
        Set("OIDC_PORTAL_CALLBACK_URI",
            "https://portal.example.com/callback");
        Set("OIDC_CONSOLE_POST_LOGOUT_URI",
            "https://console.example.com/auth/login");
        Set("OIDC_PORTAL_POST_LOGOUT_URI",
            "https://portal.example.com/auth/login");
        Set("OIDC_SEED_SVC_SECRET", $"svc-{Guid.NewGuid():N}-secret-value");
        Set("OIDC_SEED_OPS_SECRET", $"ops-{Guid.NewGuid():N}-secret-value");
        Set("DP_KEY_DIR", Path.Combine(_dir, "dpkeys"));
        Set("PORTAL_BASE_URL", "https://portal.example.com");
        Set("PRICING_ADAPTER", "disabled");
        Set("FEE_ADAPTER", "disabled");
        Set("DOC_ADAPTER", "disabled");
        Set("VAULT_ADAPTER", "disabled");
        Set("VENDOR_ADAPTER", "disabled");
        // Clear ambient dev/compose leftovers — every key not given a
        // real value above is null in the production matrix.
        foreach (var key in Keys)
            if (!_original.ContainsKey(key))
                Set(key, null);
    }

    /// Set one key inside the matrix (tracked + restored too).
    public ProdProfileEnv Override(string key, string? value)
    {
        Set(key, value);
        return this;
    }

    public static ProdProfileEnv Apply() => new();

    private void Set(string key, string? value)
    {
        if (!_original.ContainsKey(key))
            _original[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (key, value) in _original)
            Environment.SetEnvironmentVariable(key, value);
        _envWriteLock.Dispose();
        try { Directory.Delete(_dir, recursive: true); }
        catch (IOException) { /* dp keyring may linger — temp dir */ }
    }
}
