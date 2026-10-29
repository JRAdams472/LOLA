namespace Lola.Infrastructure.Pricing;

/// MPEx adapter configuration (MPE-130) — sourced from MPEX_* env
/// vars. `Audience` is sent as the optional `audience` form field on
/// the client-credentials grant; Dex stamps aud=client_id anyway, so
/// it is informational unless a deployment's IdP honors RFC 8707.
public sealed record MpexPricingOptions(
    string BaseUrl, string TokenUrl, string ClientId,
    string ClientSecret, string? Audience);
