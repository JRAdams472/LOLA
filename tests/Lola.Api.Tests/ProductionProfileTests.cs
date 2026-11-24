using Lola.Api.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-277: table-driven production-profile validation — every unsafe
/// Production configuration must throw before the host serves traffic.
public sealed class ProductionProfileTests
{
    private sealed class Env(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "lola-tests";
        public string ContentRootPath { get; set; } = ".";
        public IFileProvider ContentRootFileProvider { get; set; }
            = new NullFileProvider();
    }

    private static IConfiguration Config(Dictionary<string, string?> map)
        => new ConfigurationBuilder()
            .AddInMemoryCollection(map)
            .Build();

    /// All-disabled valid profile — every egress optional, nothing
    /// stubbed, every secret real.
    private static Dictionary<string, string?> ValidDisabled() => new()
    {
        ["CONTROL_DB_KEY"] = new string('a', 64),
        ["OPENBAO_ADDR"] = "https://bao.example.com",
        ["OPENBAO_TOKEN"] = "prod-token-abcdefghij",
        ["OIDC_SIGNING_KEY"] = "signing-pem-or-vault-blob",
        ["OIDC_ENCRYPTION_KEY"] = "encryption-pem-or-vault-blob",
        ["OIDC_ISSUER"] = "https://idp.example.com",
        ["OIDC_CONSOLE_CALLBACK_URI"] = "https://console.example.com/callback",
        ["OIDC_PORTAL_CALLBACK_URI"] = "https://portal.example.com/callback",
        ["OIDC_CONSOLE_POST_LOGOUT_URI"] = "https://console.example.com/auth/login",
        ["OIDC_PORTAL_POST_LOGOUT_URI"] = "https://portal.example.com/auth/login",
        ["OIDC_SEED_SVC_SECRET"] = "svc-prod-secret-0123456789",
        ["OIDC_SEED_OPS_SECRET"] = "ops-prod-secret-0123456789",
        ["OIDC_BFF_CONSOLE_SECRET"] = "bff-console-prod-secret-012",
        ["OIDC_BFF_PORTAL_SECRET"] = "bff-portal-prod-secret-0123",
        ["DP_KEY_DIR"] = "/run/dpkeys",
        ["PRICING_ADAPTER"] = "disabled",
        ["FEE_ADAPTER"] = "disabled",
        ["DOC_ADAPTER"] = "disabled",
        ["VAULT_ADAPTER"] = "disabled",
        ["VENDOR_ADAPTER"] = "disabled",
    };

    /// Fully-enabled valid profile — real adapters, https on every hop.
    private static Dictionary<string, string?> ValidReal() => Merge(
        ValidDisabled(), new()
        {
            ["PRICING_ADAPTER"] = "mpex",
            ["MPEX_BASE_URL"] = "https://mpex.example.com",
            ["MPEX_TOKEN_URL"] = "https://dex.example.com/token",
            ["MPEX_CLIENT_ID"] = "mpex-svc",
            ["MPEX_CLIENT_SECRET"] = "mpex-prod-secret-012345",
            ["FEE_ADAPTER"] = "zara",
            ["ZARA_BASE_URL"] = "https://zara.example.com",
            ["ZARA_TENANT_ID"] = "aa5ce000-0000-4000-8000-0000000000ac",
            ["ZARA_TOKEN"] = "zara-jwt-prod",
            ["DOC_ADAPTER"] = "isppr",
            ["ISPPR_BASE_URL"] = "https://isppr.example.com",
            ["VAULT_ADAPTER"] = "minio",
            ["S3_ENDPOINT"] = "https://s3.example.com",
            ["S3_ACCESS_KEY"] = "s3-prod-key",
            ["S3_SECRET_KEY"] = "s3-prod-secret",
            ["CLAMAV_ADDR"] = "clamav:3310",
        });

    private static Dictionary<string, string?> Merge(
        Dictionary<string, string?> a, Dictionary<string, string?> b)
    {
        var merged = new Dictionary<string, string?>(a);
        foreach (var (k, v) in b) merged[k] = v;
        return merged;
    }

    public static IEnumerable<object[]> Cases()
    {
        // (name, base config, overrides, expected message fragment —
        //  null fragment means the profile is accepted).
        yield return Row("everything omitted",
            new Dictionary<string, string?>(), null,
            "CONTROL_DB_KEY");
        yield return Row("all adapters disabled",
            ValidDisabled(), null, null);
        yield return Row("all adapters real + https",
            ValidReal(), null, null);

        // --- key/bootstrap gaps --------------------------------------
        yield return Row("no control-plane key",
            ValidDisabled(), O("CONTROL_DB_KEY", null), "CONTROL_DB_KEY");
        yield return Row("no bao token",
            ValidDisabled(), O("OPENBAO_TOKEN", null), "OPENBAO_TOKEN");
        yield return Row("no signing key",
            ValidDisabled(), O("OIDC_SIGNING_KEY", null), "OIDC_SIGNING_KEY");
        yield return Row("no encryption key",
            ValidDisabled(), O("OIDC_ENCRYPTION_KEY", null),
            "OIDC_ENCRYPTION_KEY");
        yield return Row("no dp key dir",
            ValidDisabled(), O("DP_KEY_DIR", null), "DP_KEY_DIR");
        yield return Row("no issuer",
            ValidDisabled(), O("OIDC_ISSUER", null), "OIDC_ISSUER");
        yield return Row("svc seed missing",
            ValidDisabled(), O("OIDC_SEED_SVC_SECRET", null),
            "OIDC_SEED_SVC_SECRET");
        yield return Row("svc seed too short",
            ValidDisabled(), O("OIDC_SEED_SVC_SECRET", "abc"),
            "OIDC_SEED_SVC_SECRET");
        yield return Row("svc seed is dev default",
            ValidDisabled(), O("OIDC_SEED_SVC_SECRET", "change-me"),
            "OIDC_SEED_SVC_SECRET");
        yield return Row("ops seed is dev default",
            ValidDisabled(), O("OIDC_SEED_OPS_SECRET", "lola-ops-dev"),
            "OIDC_SEED_OPS_SECRET");
        yield return Row("bff console secret missing",
            ValidDisabled(), O("OIDC_BFF_CONSOLE_SECRET", null),
            "OIDC_BFF_CONSOLE_SECRET");
        yield return Row("bff portal secret is dev default",
            ValidDisabled(), O("OIDC_BFF_PORTAL_SECRET", "change-me"),
            "OIDC_BFF_PORTAL_SECRET");

        // --- dev affordances ------------------------------------------
        yield return Row("dev seed present",
            ValidDisabled(), O("DEV_SEED_TENANT", "demo"), "DEV_SEED_TENANT");
        yield return Row("vendor stub webhook present",
            ValidDisabled(),
            O("VENDOR_STUB_WEBHOOK_URL", "https://api.example.com/hook"),
            "VENDOR_STUB_WEBHOOK_URL");
        yield return Row("doc classifier stub",
            ValidDisabled(), O("DOC_CLASSIFIER", "stub"), "DOC_CLASSIFIER");

        // --- adapter matrix -------------------------------------------
        yield return Row("pricing stub",
            ValidDisabled(), O("PRICING_ADAPTER", "stub"),
            "PRICING_ADAPTER");
        yield return Row("pricing unset",
            ValidDisabled(), O("PRICING_ADAPTER", null),
            "PRICING_ADAPTER");
        yield return Row("pricing unknown",
            ValidDisabled(), O("PRICING_ADAPTER", "notreal"),
            "PRICING_ADAPTER");
        yield return Row("fees stub",
            ValidDisabled(), O("FEE_ADAPTER", "stub"), "FEE_ADAPTER");
        yield return Row("docs stub",
            ValidDisabled(), O("DOC_ADAPTER", "stub"), "DOC_ADAPTER");
        yield return Row("vault stub",
            ValidDisabled(), O("VAULT_ADAPTER", "stub"), "VAULT_ADAPTER");
        yield return Row("vault unset",
            ValidDisabled(), O("VAULT_ADAPTER", null), "VAULT_ADAPTER");
        yield return Row("vendor stub",
            ValidDisabled(), O("VENDOR_ADAPTER", "stub"),
            "VENDOR_ADAPTER");
        yield return Row("vendor unset",
            ValidDisabled(), O("VENDOR_ADAPTER", null), "VENDOR_ADAPTER");
        yield return Row("minio missing s3 endpoint",
            ValidReal(), O("S3_ENDPOINT", null), "S3_ENDPOINT");
        yield return Row("mpex missing client secret",
            ValidReal(), O("MPEX_CLIENT_SECRET", null),
            "MPEX_CLIENT_SECRET");
        yield return Row("zara missing token",
            ValidReal(), M(("ZARA_TOKEN", null), ("ZARA_TOKEN_FILE", null)),
            "ZARA_TOKEN");

        // --- transport -------------------------------------------------
        yield return Row("http openbao",
            ValidDisabled(), O("OPENBAO_ADDR", "http://bao:8200"),
            "OPENBAO_ADDR");
        yield return Row("http issuer",
            ValidDisabled(), O("OIDC_ISSUER", "http://idp.example.com"),
            "OIDC_ISSUER");
        yield return Row("http console callback",
            ValidDisabled(),
            O("OIDC_CONSOLE_CALLBACK_URI",
                "http://console.example.com/callback"),
            "OIDC_CONSOLE_CALLBACK_URI");
        yield return Row("http portal post-logout",
            ValidDisabled(),
            O("OIDC_PORTAL_POST_LOGOUT_URI",
                "http://portal.example.com/auth/login"),
            "OIDC_PORTAL_POST_LOGOUT_URI");
        yield return Row("http mpex base",
            ValidReal(), O("MPEX_BASE_URL", "http://mpex:8080"),
            "MPEX_BASE_URL");
        yield return Row("http s3 non-loopback allowlisted",
            ValidReal(),
            M(("S3_ENDPOINT", "http://minio:9000"),
              ("INSECURE_ENDPOINT_ALLOWLIST", "S3_ENDPOINT")),
            "S3_ENDPOINT");
        yield return Row("http s3 loopback allowlisted",
            ValidReal(),
            M(("S3_ENDPOINT", "http://localhost:9200"),
              ("INSECURE_ENDPOINT_ALLOWLIST", "S3_ENDPOINT")),
            null);
        yield return Row("http s3 loopback NOT allowlisted",
            ValidReal(), O("S3_ENDPOINT", "http://localhost:9200"),
            "S3_ENDPOINT");
        yield return Row("http openbao loopback allowlisted",
            ValidDisabled(),
            M(("OPENBAO_ADDR", "http://localhost:8300"),
              ("INSECURE_ENDPOINT_ALLOWLIST", "OPENBAO_ADDR")),
            null);
        yield return Row("http cors origin non-loopback",
            ValidDisabled(), O("CORS_ORIGIN", "http://ui.example.com"),
            "CORS_ORIGIN");
        yield return Row("non-uri issuer",
            ValidDisabled(), O("OIDC_ISSUER", "idp.example.com"),
            "OIDC_ISSUER");

        // --- forwarded headers ------------------------------------------
        yield return Row("proxy wildcard",
            ValidDisabled(), O("TRUSTED_PROXIES", "*"),
            "TRUSTED_PROXIES");
        yield return Row("proxy zero cidr",
            ValidDisabled(), O("TRUSTED_PROXIES", "0.0.0.0/0"),
            "TRUSTED_PROXIES");
        yield return Row("proxy ipv6 zero cidr",
            ValidDisabled(), O("TRUSTED_PROXIES", "::/0"),
            "TRUSTED_PROXIES");
        yield return Row("proxy junk",
            ValidDisabled(), O("TRUSTED_PROXIES", "edge-host"),
            "TRUSTED_PROXIES");
        yield return Row("proxy explicit ips + cidr",
            ValidDisabled(),
            O("TRUSTED_PROXIES", "10.0.0.1, 192.168.0.0/16, ::1"),
            null);
    }

    private static object[] Row(string name,
        Dictionary<string, string?> cfg,
        KeyValuePair<string, string?>[]? overrides,
        string? fragment)
    {
        var merged = new Dictionary<string, string?>(cfg);
        if (overrides is not null)
            foreach (var (k, v) in overrides) merged[k] = v;
        return [name, merged, fragment!];
    }

    private static KeyValuePair<string, string?>[] O(string k, string? v)
        => [new(k, v)];

    private static KeyValuePair<string, string?>[] M(
        params (string k, string? v)[] pairs)
        => pairs.Select(p => new KeyValuePair<string, string?>(p.k, p.v))
            .ToArray();

    [Theory]
    [MemberData(nameof(Cases))]
    public void Production_profile(string name,
        Dictionary<string, string?> cfg, string? expectFragment)
    {
        var env = new Env(Environments.Production);
        if (expectFragment is null)
        {
            // Must not throw.
            ProductionProfile.Validate(Config(cfg), env);
            return;
        }
        var ex = Should.Throw<InvalidOperationException>(
            () => ProductionProfile.Validate(Config(cfg), env),
            $"case '{name}' should fail closed");
        ex.Message.ShouldContain(expectFragment);
    }

    [Fact]
    public void Development_is_untouched()
    {
        // An empty config in Development must validate silently —
        // the dev/CI path never trips the production gate.
        ProductionProfile.Validate(
            Config(new Dictionary<string, string?>()),
            new Env(Environments.Development));
    }
}
