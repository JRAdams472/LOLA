using System.Net;

namespace Lola.Api.Hosting;

/// Fail-closed production profile (MPE-277, security-remediation phase 8).
/// <see cref="Validate"/> runs before any service registration — an
/// unsafe Production configuration throws before the host can serve a
/// single protected request. Development and CI are untouched: every
/// rule here is gated on <see cref="IHostEnvironment.IsProduction"/>.
/// Violation messages name config KEYS only — never values (invariant 3
/// applies to startup diagnostics too).
public static class ProductionProfile
{
    /// Config keys whose absolute-URI values must be https in Production
    /// when their capability is enabled — invariant 5, TLS on every hop.
    /// `INSECURE_ENDPOINT_ALLOWLIST` (comma-separated key names) permits
    /// a LOOPBACK http endpoint only, for a TLS-terminating sidecar —
    /// the runbook documents that both sides of that proxy must still
    /// be protected transport. Non-loopback http is never allowed.
    private static readonly string[] HttpsKeys =
    [
        "OPENBAO_ADDR", "OIDC_ISSUER",
        "OIDC_CONSOLE_CALLBACK_URI", "OIDC_PORTAL_CALLBACK_URI",
        "OIDC_CONSOLE_POST_LOGOUT_URI", "OIDC_PORTAL_POST_LOGOUT_URI",
        "MPEX_BASE_URL", "MPEX_TOKEN_URL", "ZARA_BASE_URL",
        "ISPPR_BASE_URL", "S3_ENDPOINT", "PORTAL_BASE_URL", "CORS_ORIGIN",
    ];

    /// Dev-default values that must never appear in a Production config —
    /// an operator who left the compose/.env.example defaults in place
    /// gets a boot failure, not a silently weak deployment.
    private static readonly IReadOnlySet<string> ForbiddenSecrets =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "change-me", "change-me-ops",
            "lola-svc-dev", "lola-ops-dev",
            "dev-session-secret-not-for-prod",
            "dev-portal-session-secret-not-for-prod",
        };

    /// Every DEV_SEED_* key — the demo-tenant/login bootstrap is
    /// dev/e2e only and must not exist in Production at all.
    private static readonly string[] DevSeedKeys =
    [
        "DEV_SEED_TENANT", "DEV_SEED_TENANT_SLUG", "DEV_SEED_TENANT_NAME",
        "DEV_SEED_ADMIN_EMAIL", "DEV_SEED_ADMIN_PASSWORD",
        "DEV_SEED_ADMIN_NAME", "DEV_SEED_OFFICER_EMAIL",
        "DEV_SEED_OFFICER_PASSWORD", "DEV_SEED_OFFICER_NAME",
    ];

    public static void Validate(IConfiguration config, IHostEnvironment env)
    {
        if (!env.IsProduction()) return;

        var violations = new List<string>();

        // --- keys / bootstrap ------------------------------------------
        if (string.IsNullOrWhiteSpace(config["CONTROL_DB_KEY"])
            && !FileExists(config["CONTROL_DB_KEY_FILE"]))
            violations.Add(
                "CONTROL_DB_KEY or CONTROL_DB_KEY_FILE is required " +
                "in Production — the control plane never opens unkeyed");

        if (string.IsNullOrWhiteSpace(config["OIDC_SIGNING_KEY"])
            && !FileExists(config["OIDC_SIGNING_KEY_FILE"]))
            violations.Add(
                "OIDC_SIGNING_KEY or OIDC_SIGNING_KEY_FILE is required " +
                "in Production — dev keys are never generated there");
        if (string.IsNullOrWhiteSpace(config["OIDC_ENCRYPTION_KEY"])
            && !FileExists(config["OIDC_ENCRYPTION_KEY_FILE"]))
            violations.Add(
                "OIDC_ENCRYPTION_KEY or OIDC_ENCRYPTION_KEY_FILE is required " +
                "in Production — dev keys are never generated there");

        if (string.IsNullOrWhiteSpace(config["DP_KEY_DIR"]))
            violations.Add(
                "DP_KEY_DIR is required in Production — the lola.auth " +
                "data-protection keyring needs a mounted persistent directory");

        // --- scoped OpenBao --------------------------------------------
        var baoToken = config["OPENBAO_TOKEN"];
        var baoTokenFile = config["OPENBAO_TOKEN_FILE"];
        if (string.IsNullOrWhiteSpace(baoToken)
            && !FileExists(baoTokenFile))
            violations.Add(
                "OPENBAO_TOKEN or an existing OPENBAO_TOKEN_FILE is required " +
                "in Production — scoped OpenBao access is mandatory");

        // --- OIDC service-client bootstrap ------------------------------
        RequireSecret(config, "OIDC_SEED_SVC_SECRET", violations);
        RequireSecret(config, "OIDC_SEED_OPS_SECRET", violations);
        // BFF session-store clients (MPE-289) — the custody surface
        // denies callers without a minted namespace claim.
        RequireSecret(config, "OIDC_BFF_CONSOLE_SECRET", violations);
        RequireSecret(config, "OIDC_BFF_PORTAL_SECRET", violations);

        // --- dev affordances that must be absent -----------------------
        foreach (var key in DevSeedKeys)
            if (!string.IsNullOrWhiteSpace(config[key]))
                violations.Add($"{key} must not be set in Production " +
                    "— demo identities are dev/e2e only");
        if (!string.IsNullOrWhiteSpace(config["VENDOR_STUB_WEBHOOK_URL"]))
            violations.Add(
                "VENDOR_STUB_WEBHOOK_URL must not be set in Production — " +
                "it drives the stub adapter's simulated vendor push");

        // --- adapter selection ------------------------------------------
        RequireAdapter(config, "PRICING_ADAPTER", ["mpex", "disabled"], violations);
        RequireAdapter(config, "FEE_ADAPTER", ["zara", "disabled"], violations);
        RequireAdapter(config, "DOC_ADAPTER", ["isppr", "disabled"], violations);
        RequireAdapter(config, "VAULT_ADAPTER", ["minio", "disabled"], violations);
        RequireAdapter(config, "VENDOR_ADAPTER", ["disabled"], violations);
        if ("stub".Equals(config["DOC_CLASSIFIER"],
                StringComparison.OrdinalIgnoreCase))
            violations.Add("DOC_CLASSIFIER=stub is not allowed in Production");

        // Enabled adapters need their egress config present — checked
        // here (not just at registration) so the https hop rules below
        // apply to every URL the deployment will actually call.
        RequireAdapterConfig(config, "PRICING_ADAPTER", "mpex",
            ["MPEX_BASE_URL", "MPEX_TOKEN_URL", "MPEX_CLIENT_ID",
             "MPEX_CLIENT_SECRET"], violations);
        RequireAdapterConfig(config, "FEE_ADAPTER", "zara",
            ["ZARA_BASE_URL", "ZARA_TENANT_ID"], violations);
        if ("zara".Equals(config["FEE_ADAPTER"],
                StringComparison.OrdinalIgnoreCase)
            && string.IsNullOrWhiteSpace(config["ZARA_TOKEN"])
            && string.IsNullOrWhiteSpace(config["ZARA_TOKEN_FILE"]))
            violations.Add("ZARA_TOKEN or ZARA_TOKEN_FILE is required " +
                "when FEE_ADAPTER=zara");
        RequireAdapterConfig(config, "DOC_ADAPTER", "isppr",
            ["ISPPR_BASE_URL"], violations);
        RequireAdapterConfig(config, "VAULT_ADAPTER", "minio",
            ["S3_ENDPOINT", "S3_ACCESS_KEY", "S3_SECRET_KEY",
             "CLAMAV_ADDR"], violations);

        // --- transport ---------------------------------------------------
        var allowlist = ParseList(config["INSECURE_ENDPOINT_ALLOWLIST"]);
        foreach (var key in HttpsKeys)
            RequireHttps(config, key, allowlist, violations);

        // --- forwarded headers -------------------------------------------
        var proxies = config["TRUSTED_PROXIES"];
        if (!string.IsNullOrWhiteSpace(proxies))
            ValidateProxies(proxies, violations);

        if (violations.Count > 0)
            throw new InvalidOperationException(
                "Production configuration failed validation — refusing to " +
                "start (see docs/deployment-runbook.md):\n  - " +
                string.Join("\n  - ", violations));
    }

    private static void RequireAdapter(IConfiguration config, string key,
        string[] allowed, List<string> violations)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value)
            || !allowed.Contains(value, StringComparer.OrdinalIgnoreCase))
            violations.Add(
                $"{key} must be one of [{string.Join(", ", allowed)}] " +
                $"in Production — stub/unset fails closed");
    }

    private static void RequireAdapterConfig(IConfiguration config,
        string adapterKey, string enabledValue, string[] requiredKeys,
        List<string> violations)
    {
        if (!enabledValue.Equals(config[adapterKey],
                StringComparison.OrdinalIgnoreCase))
            return;
        foreach (var key in requiredKeys)
            if (string.IsNullOrWhiteSpace(config[key]))
                violations.Add($"{key} is required when " +
                    $"{adapterKey}={enabledValue}");
    }

    private static void RequireSecret(IConfiguration config, string key,
        List<string> violations)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value) || value.Length < 16
            || ForbiddenSecrets.Contains(value))
            violations.Add(
                $"{key} is required in Production (≥16 chars, not a " +
                "repo dev default) — the control-plane service clients " +
                "are the only platform bootstrap");
    }

    private static void RequireHttps(IConfiguration config, string key,
        IReadOnlySet<string> allowlist, List<string> violations)
    {
        var value = config[key];
        if (string.IsNullOrWhiteSpace(value))
        {
            // Optional keys (CORS_ORIGIN, egress of disabled adapters)
            // may be unset; the adapter matrix above already forces the
            // enabled ones to exist.
            if (key is "OIDC_ISSUER" or "OPENBAO_ADDR")
                violations.Add($"{key} is required in Production");
            return;
        }
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            violations.Add($"{key} must be an absolute URI in Production");
            return;
        }
        if (uri.Scheme == Uri.UriSchemeHttps) return;
        if (allowlist.Contains(key) && IsLoopback(uri)) return;
        violations.Add(
            $"{key} must be https in Production (loopback http requires " +
            $"the key's name in INSECURE_ENDPOINT_ALLOWLIST)");
    }

    private static bool IsLoopback(Uri uri)
        => uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase)
            || IPAddress.TryParse(uri.Host, out var ip)
                && IPAddress.IsLoopback(ip);

    private static void ValidateProxies(string proxies,
        List<string> violations)
    {
        foreach (var entry in proxies.Split(',',
                     StringSplitOptions.RemoveEmptyEntries
                     | StringSplitOptions.TrimEntries))
        {
            var slash = entry.IndexOf('/');
            if (slash < 0)
            {
                if (!IPAddress.TryParse(entry, out _))
                    violations.Add(
                        $"TRUSTED_PROXIES entry must be an IP or CIDR " +
                        $"— forwarded headers are never trusted broadly");
                continue;
            }
            var ip = entry[..slash];
            if (!IPAddress.TryParse(ip, out var addr)
                || !int.TryParse(entry[(slash + 1)..], out var bits)
                || bits < 0
                || bits > (addr.AddressFamily
                    == System.Net.Sockets.AddressFamily.InterNetwork
                    ? 32 : 128))
            {
                violations.Add("TRUSTED_PROXIES CIDR entries must be " +
                    "valid ip/prefix — forwarded headers are never " +
                    "trusted broadly");
                continue;
            }
            // A zero-prefix network trusts every source — equivalent to
            // trusting forwarded headers from the open internet.
            if (bits == 0)
                violations.Add("TRUSTED_PROXIES must not contain a " +
                    "zero-length CIDR — that trusts every client");
        }
        if (proxies.Trim() == "*")
            violations.Add("TRUSTED_PROXIES must not contain '*'");
    }

    private static IReadOnlySet<string> ParseList(string? raw)
        => string.IsNullOrWhiteSpace(raw)
            ? new HashSet<string>(StringComparer.Ordinal)
            : raw.Split(',', StringSplitOptions.RemoveEmptyEntries
                | StringSplitOptions.TrimEntries)
                .ToHashSet(StringComparer.Ordinal);

    private static bool FileExists(string? path)
        => !string.IsNullOrWhiteSpace(path) && File.Exists(path);
}
