using System.Text.Json;
using System.Text.Json.Nodes;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.Application.Vendors;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Wolverine;

namespace Lola.Api.Vendors;

/// Vendor webhook ingress (MPE-228) — `POST /api/vendor-webhooks/
/// {kind}`; the codebase's first push channel (ADR-0011 REST).
/// Vendors hold no OIDC tokens — `X-Vendor-Signature` HMAC over the
/// raw body, keyed by the tenant's OpenBao-KV `webhook_secret`, IS
/// the authentication. Body is stream-bounded and the route carries
/// its own per-IP rate policy; replay dedupes at the order level —
/// an already-terminal order answers 200 with no second write.
public static class VendorWebhookEndpoints
{
    private const int MaxBody = 64 * 1024;

    public static void MapVendorWebhookEndpoints(this WebApplication app)
        => app.MapPost("/api/vendor-webhooks/{kind}", HandleAsync)
            .RequireRateLimiting("vendor-webhook");

    private static async Task<IResult> HandleAsync(HttpRequest request,
        string kind, ITenantRegistry registry, IMessageBus bus,
        CancellationToken ct)
    {
        VendorKind vk;
        try { vk = VendorGuards.Parse(kind); }
        catch { return Results.NotFound(); }

        if (request.ContentLength > MaxBody)
            return Results.StatusCode(StatusCodes
                .Status413PayloadTooLarge);
        var body = await ReadBoundedAsync(request, ct);
        if (body is null)
            return Results.StatusCode(StatusCodes
                .Status413PayloadTooLarge);

        JsonObject? payload;
        try
        {
            payload = JsonNode.Parse(
                System.Text.Encoding.UTF8.GetString(body)) as JsonObject;
        }
        catch { return Results.BadRequest(); }
        var tenantId = payload?["tenant"]?.GetValue<string>();
        var externalId = payload?["external_id"]?.GetValue<string>();
        var outcome = payload?["outcome"]?.GetValue<string>();
        var report = payload?["report"] as JsonObject;
        if (tenantId is null || externalId is null ||
            outcome is null || report is null)
            return Results.BadRequest();

        var tenant = await registry.FindByIdAsync(tenantId, ct);
        if (tenant is null || tenant.Status != "active")
            return Results.Unauthorized();

        using var _ = TenantContext.Use(TenantScope.From(tenant));
        // CipherContext scopes the transit key — CompleteVendorOrder
        // encrypts the result envelope (unauthenticated path: no
        // middleware sets it here).
        using var __ = CipherContext.Use(tenant.Id);
        var creds = await request.HttpContext.RequestServices
            .GetRequiredService<IVendorCredentialSource>()
            .ForAsync(vk, ct);
        if (creds?.WebhookSecret is null ||
            !VendorWebhookAuth.Verify(
                request.Headers[VendorWebhookAuth.SignatureHeader],
                body, creds.WebhookSecret))
            return Results.Unauthorized();

        var verdict = await bus.InvokeAsync<VendorCompletion>(
            new CompleteVendorOrderCommand(vk, externalId,
                payload!["request_hash"]?.GetValue<string>(),
                outcome,
                payload!["failure_reason"]?.GetValue<string>(),
                report, $"webhook:{VendorGuards.Token(vk)}"), ct);
        return verdict switch
        {
            VendorCompletion.Applied => Results.Ok(
                new { status = "completed" }),
            VendorCompletion.Duplicate => Results.Ok(
                new { status = "duplicate" }),
            VendorCompletion.HashMismatch => Results.Conflict(
                new { status = "request_hash_mismatch" }),
            VendorCompletion.Ambiguous => Results.Conflict(
                new { status = "ambiguous_external_id" }),
            _ => Results.NotFound()
        };
    }

    /// Content-Length can be absent (chunked) — bound the actual
    /// stream, not just the declared header.
    private static async Task<byte[]?> ReadBoundedAsync(
        HttpRequest request, CancellationToken ct)
    {
        await using var ms = new MemoryStream();
        var buffer = new byte[8192];
        var total = 0;
        int read;
        while ((read = await request.Body.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > MaxBody) return null;
            await ms.WriteAsync(buffer.AsMemory(0, read), ct);
        }
        return ms.ToArray();
    }
}
