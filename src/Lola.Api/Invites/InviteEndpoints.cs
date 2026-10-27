using Lola.Application.Admin.Borrowers;
using Wolverine;

namespace Lola.Api.Invites;

/// Public invite accept — no auth (the token is the credential).
/// Rate-limited per IP alongside /connect/token (MPE-87).
public static class InviteEndpoints
{
    public static void MapInviteEndpoints(this WebApplication app)
    {
        app.MapPost("/api/invites/accept", async (
                AcceptInviteRequest body, IMessageBus bus, CancellationToken ct) =>
            Results.Ok(await bus.InvokeAsync<AcceptInviteResult>(
                new AcceptInviteCommand(body.Token, body.DisplayName, body.Password), ct)))
            .RequireRateLimiting("auth-tight")
            .AllowAnonymous();
    }

    public sealed record AcceptInviteRequest(string Token, string DisplayName, string Password);
}
