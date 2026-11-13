using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
using Lola.Domain.Identity;
using Microsoft.Net.Http.Headers;

namespace Lola.Api.Vault;

/// Vault capability download (MPE-202): `GET /api/vault-content/{docId}?tid&exp&sig`.
/// Auth-free like any presigned URL — `sig` is the credential. `tid`
/// selects the tenant (it is also inside the signed payload, so the
/// scope can't be re-anchored); a valid TenantContext + CipherContext
/// are bound before the repo/vault are touched, and every bad input
/// collapses to the same 403 — no oracle on which part failed.
/// Plaintext exists only in this response stream: the object crosses
/// the store boundary as ciphertext and VaultCipher opens it on the
/// way out (invariant 1).
public static class VaultEndpoints
{
    public static void MapVaultEndpoints(this WebApplication app)
        => app.MapGet("/api/vault-content/{docId}", HandleAsync)
            .AllowAnonymous()
            .RequireRateLimiting("intake");

    private static async Task<IResult> HandleAsync(string docId,
        string? tid, string? exp, string? sig, HttpContext ctx,
        ITenantRegistry registry, IVaultUrlSigner signer,
        IVaultDocRepository repo, IObjectVault store,
        VaultCipher cipher, CancellationToken ct)
    {
        if (tid is null || sig is null
            || !long.TryParse(exp, out var expUnix))
            return Denied();
        var tenant = await registry.FindByIdAsync(tid, ct);
        if (tenant is null || !"active".Equals(tenant.Status,
                StringComparison.OrdinalIgnoreCase))
            return Denied();

        using var _t = TenantContext.Use(TenantScope.From(tenant));
        using var _c = CipherContext.Use(tenant.Id);
        if (!await signer.VerifyAsync(docId,
                DateTimeOffset.FromUnixTimeSeconds(expUnix), sig, ct))
            return Denied();

        var doc = await repo.GetAsync(docId, ct);
        if (doc is null) return Results.NotFound();
        if (doc.Status != "clean")
            return Results.Problem(
                statusCode: StatusCodes.Status409Conflict,
                title: "upload is not clean");

        await using var ciphertext =
            await store.GetAsync(doc.S3Key, ct);
        await using var plaintext =
            await cipher.OpenAsync(doc.WrappedDek, ciphertext, ct);
        ctx.Response.ContentType = doc.ContentType;
        // Plaintext protected data must never linger in a browser or
        // intermediary cache; sniffed type + attachment + nosniff keep
        // the response inert outside the save dialog.
        ctx.Response.Headers.CacheControl = "no-store";
        ctx.Response.Headers.XContentTypeOptions = "nosniff";
        // filename* carries the borrower's own name back — RFC5987
        // encoding, so arbitrary upload names can never inject a
        // header. No filename= fallback: every modern client reads
        // filename*, and a dual header is an injection surface.
        ctx.Response.Headers.ContentDisposition =
            new ContentDispositionHeaderValue("attachment")
            { FileNameStar = doc.Filename }.ToString();
        await plaintext.CopyToAsync(ctx.Response.Body, ct);
        return Results.Empty;
    }

    private static IResult Denied() => Results.Problem(
        statusCode: StatusCodes.Status403Forbidden,
        title: "invalid or expired vault signature");
}
