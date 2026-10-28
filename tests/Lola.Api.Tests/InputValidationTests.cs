using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Dapper;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-90: boundary validation on identity inputs — email format,
/// password policy (>=12 chars), display-name bounds — enforced in the
/// handlers and surfaced as 400 problem details.
public sealed class InputValidationTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Admin_create_paths_reject_bad_inputs()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var token = await PkceLogin.TokenAsync(
            factory, tenant, LolaRole.TenantAdmin);
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);

        // Invite create — malformed email.
        (await client.PostAsJsonAsync($"/api/admin/tenants/{tenant}/invites",
                new { email = "not-an-email" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Officer create — malformed email, short password, long name.
        (await client.PostAsJsonAsync($"/api/admin/tenants/{tenant}/users",
                new { email = "bad", displayName = "X", password = (string?)null }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"/api/admin/tenants/{tenant}/users",
                new { email = $"officer-{tenant}@test.dev", displayName = "X", password = "short" }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await client.PostAsJsonAsync($"/api/admin/tenants/{tenant}/users",
                new { email = $"officer2-{tenant}@test.dev", displayName = new string('x', 201), password = (string?)null }))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Happy paths still work — invited officer (no password) and a
        // valid invite both succeed.
        var officer = await client.PostAsJsonAsync($"/api/admin/tenants/{tenant}/users",
            new { email = $"officer3-{tenant}@test.dev", displayName = "Officer", password = (string?)null });
        officer.StatusCode.ShouldBe(HttpStatusCode.Created);
        (await officer.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("status").GetString().ShouldBe("invited");
        (await client.PostAsJsonAsync($"/api/admin/tenants/{tenant}/invites",
                new { email = $"borrower-{tenant}@test.dev" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);
    }

    [Fact]
    public async Task Accept_invite_validates_inputs_before_lookup()
    {
        // Seed a tenant + a live invite directly (token known in test).
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var db = factory.Services.GetRequiredService<ControlPlaneDb>();
        var registry = new TenantRegistry(db);
        await registry.InsertProvisioningAsync(
            tenant, tenant, $"Tenant {tenant}", $"data/tenants/{tenant}.db");
        await registry.SetStatusAsync(tenant, "active");

        var inviteToken = "valid-invite-token-0123";
        var hash = Convert.ToHexString(SHA256.HashData(
            Encoding.ASCII.GetBytes(inviteToken))).ToLowerInvariant();
        using (var conn = db.Open())
        {
            await conn.ExecuteAsync("""
                INSERT INTO invites (id, tenant_id, email, role, token_hash,
                                     expires_utc, created_by)
                VALUES (@id, @t, @e, 'Borrower', @h, @exp, 'test')
                """, new
            {
                id = $"i-{Guid.NewGuid():N}",
                t = tenant,
                e = $"borrower-{tenant}@test.dev",
                h = hash,
                exp = DateTimeOffset.UtcNow.AddHours(1).ToString("O")
            });
        }

        var client = factory.CreateClient();
        Task<HttpResponseMessage> Accept(string token, string name, string pw) =>
            client.PostAsJsonAsync("/api/invites/accept",
                new { token, displayName = name, password = pw });

        // Boundary failures answer 400 before invite lookup — identical
        // shape whether or not the token is real.
        (await Accept(inviteToken, "Borrower", "short"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Accept(inviteToken, new string('x', 201), PkceLogin.Password))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);
        (await Accept("bogus", "Borrower", PkceLogin.Password))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // The live invite still accepts afterward — rejections didn't
        // burn it.
        (await Accept(inviteToken, "Borrower One", PkceLogin.Password))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
    }
}
