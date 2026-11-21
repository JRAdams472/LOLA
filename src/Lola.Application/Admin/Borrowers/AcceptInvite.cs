using System.Security.Cryptography;
using Lola.Application.Errors;
using Lola.Application.Identity;

namespace Lola.Application.Admin.Borrowers;

public sealed record AcceptInviteCommand(string Token, string DisplayName, string Password);

public sealed record AcceptInviteResult(string UserId, string TenantId, string Status);

/// Public set-password accept (MPE-87; partner roles M19): the token
/// IS the tenant binding — no tenant context exists yet, and none is
/// required beyond the invite row. All state changes — protected
/// system-group resolve, user insert, membership, single-use consume —
/// commit atomically inside IInviteAcceptance's one control-plane
/// transaction (MPE-273): a failure mid-flow or a lost concurrency
/// race leaves no orphan account and no half grant. Argon2 runs
/// BEFORE the call so hashing never holds the SQLite write lock.
public static class AcceptInviteHandler
{
    public static async Task<AcceptInviteResult> Handle(
        AcceptInviteCommand c, IInviteAcceptance acceptance,
        IPasswordHasher hasher, CancellationToken ct)
    {
        // Cheap boundary validation before any lookup — a malformed
        // request fails identically whether or not the token is real.
        if (string.IsNullOrWhiteSpace(c.Token))
            throw DomainException.BadRequest("token is required");
        var displayName = InputValidation.RequireDisplayName(c.DisplayName);
        InputValidation.RequirePassword(c.Password);

        var hash = Convert.ToHexString(SHA256.HashData(
            System.Text.Encoding.ASCII.GetBytes(c.Token))).ToLowerInvariant();
        var passwordHash = hasher.Hash(c.Password);

        var outcome = await acceptance.AcceptAsync(
            new InviteAcceptanceRequest(hash, displayName, passwordHash), ct);
        return new AcceptInviteResult(outcome.UserId, outcome.TenantId, "active");
    }
}
