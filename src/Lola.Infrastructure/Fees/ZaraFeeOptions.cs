namespace Lola.Infrastructure.Fees;

/// Zara adapter configuration (MPE-183) — sourced from ZARA_* env
/// vars. `Env` selects the evaluate partition (dev|qa|uat|prod);
/// `TenantId` is the Zara-side tenant (slug or UUID) stamped into
/// both the request body and the JWT `tenant_id` claim.
///
/// Token source order: `Token` (env-injected JWT) wins, else
/// `TokenFile` is read — and re-read when the cached token nears
/// expiry, so an external re-mint (zara_devtoken cron, secret
/// rotation) heals without a restart.
public sealed record ZaraFeeOptions(
    string BaseUrl, string Env, string TenantId,
    string? TokenFile, string? Token);
