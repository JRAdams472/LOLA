using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Lola.Application.Audit;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Microsoft.Extensions.DependencyInjection;
using Shouldly;

namespace Lola.Api.Tests;

/// MPE-119 marquee — the M6 arc end-to-end: provision → submit →
/// masked read vs officer reveal → blind-search event → chain verify →
/// tampered row fails verify → retention gate blocks → force+reason
/// override → erase → residuals gone + users anonymized + audit spine
/// retained. The eraser runs for real here (keys faked — no OpenBao —
/// but file deletion and control-plane residuals are live).
public sealed class ErasureMarqueeTests(LolaApiFactory factory)
    : IClassFixture<LolaApiFactory>
{
    [Fact]
    public async Task Audit_retention_erasure_full_arc()
    {
        var tenant = $"t{Guid.NewGuid():N}"[..12];
        var svc = factory.CreateClient();
        svc.DefaultRequestHeaders.Authorization =
            new("Bearer", await ServiceTokenAsync(svc));
        (await svc.PostAsJsonAsync("/api/admin/tenants",
            new { tenantId = tenant, slug = tenant, displayName = "Marq Co" }))
            .StatusCode.ShouldBe(HttpStatusCode.Created);

        // A live svc-{tenant} credential + token row to shred later.
        await factory.ResetServiceClientSecretAsync(tenant, "marq-secret");
        var tenantSvc = factory.CreateClient();
        (await tenantSvc.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = $"svc-{tenant}",
                ["client_secret"] = "marq-secret",
                ["scope"] = "lola-api"
            }))).StatusCode.ShouldBe(HttpStatusCode.OK);

        // Officer login → user/group rows + OIDC authorization/token.
        var officer = factory.CreateClient();
        officer.DefaultRequestHeaders.Authorization =
            new("Bearer", await PkceLogin.TokenAsync(factory, tenant, LolaRole.LoanOfficer));
        // A second identity whose tombstone we assert post-erase.
        await factory.SeedUserAsync(tenant,
            $"b-{tenant}@test.dev", PkceLogin.Password, LolaRole.Borrower);
        var base_ = $"/api/tenants/{tenant}/applications";

        // draft → submit (full valid patch).
        var appId = (await (await officer.PostAsJsonAsync(base_, new { }))
            .Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetString()!;
        (await officer.PatchAsJsonAsync($"{base_}/{appId}",
            new { patch = FullPatch }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);
        (await officer.PostAsJsonAsync($"{base_}/{appId}/submit", new { }))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // Masked read hides the SSN; an officer reveal returns it and
        // writes the audited reveal event.
        (await (await officer.GetAsync($"{base_}/{appId}"))
            .Content.ReadAsStringAsync()).ShouldNotContain("123456789");
        var reveal = await officer.PostAsJsonAsync($"{base_}/{appId}/reveal",
            new { reason = "marquee" });
        reveal.StatusCode.ShouldBe(HttpStatusCode.OK);
        (await reveal.Content.ReadAsStringAsync()).ShouldContain("123456789");

        // Blind-index search is an audited PII access too.
        (await officer.GetAsync($"{base_}/search?field=ssn&value=123456789"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        // The hash chain verifies — then a tampered row breaks it.
        var ok = await officer.GetFromJsonAsync<JsonElement>($"{base_}/audit/verify");
        ok.GetProperty("ok").GetBoolean().ShouldBeTrue();
        var before = ok.GetProperty("eventCount").GetInt32();
        before.ShouldBeGreaterThanOrEqualTo(3);

        (factory.Services.GetRequiredService<ITenantAuditLog>()
            as FakeTenantAuditLog)!.TamperForTest(1);
        var broken = await officer.GetFromJsonAsync<JsonElement>(
            $"{base_}/audit/verify");
        broken.GetProperty("ok").GetBoolean().ShouldBeFalse();
        broken.GetProperty("firstBrokenSeq").GetInt64().ShouldBe(1);

        // Seed residuals the lifecycle must clear: an outstanding
        // invite and a rollout row that must SURVIVE (audit spine).
        var cp = factory.Services.GetRequiredService<ControlPlaneDb>();
        using (var conn = cp.Open())
        {
            await conn.ExecuteAsync(
                """
                INSERT INTO invites (id, tenant_id, email, role, token_hash,
                                     expires_utc, created_by)
                VALUES (@i, @t, 'inv@x.dev', 'Borrower', @h, '2030-01-01', 'seed')
                """, new { i = $"i-{tenant}", t = tenant, h = $"h-{tenant}" });
            await conn.ExecuteAsync(
                """
                INSERT INTO tenant_migrations (tenant_id, version, name, status)
                VALUES (@t, 1, '0001_baseline', 'applied')
                """, new { t = tenant });
        }

        // Retention gate: inside the 25-month window a plain DELETE is
        // refused; force without a reason is a 400.
        (await svc.DeleteAsync($"/api/admin/tenants/{tenant}"))
            .StatusCode.ShouldBe(HttpStatusCode.Conflict);
        (await svc.DeleteAsync($"/api/admin/tenants/{tenant}?force=true"))
            .StatusCode.ShouldBe(HttpStatusCode.BadRequest);

        // Force + reason overrides — keys die, file dies, residuals die,
        // the tenant retires, and the override lands in the spine.
        (await svc.DeleteAsync(
                $"/api/admin/tenants/{tenant}?force=true&reason=reg_b_expired"))
            .StatusCode.ShouldBe(HttpStatusCode.OK);

        using (var conn = cp.Open())
        {
            // Residuals: invites, groups, memberships, roles all gone.
            // (This factory's control DB holds only this test's tenant
            // rows, so totals are the honest assertion.)
            (await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM invites WHERE tenant_id = @t",
                new { t = tenant })).ShouldBe(0);
            (await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM groups WHERE tenant_id = @t",
                new { t = tenant })).ShouldBe(0);
            (await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM user_groups")).ShouldBe(0);
            (await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM group_roles")).ShouldBe(0);

            // svc-{tenant} is GONE — and inert: token mint now fails.
            (await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM openiddict_applications WHERE client_id = @c",
                new { c = $"svc-{tenant}" })).ShouldBe(0);
            // Every OIDC row attributable to the tenant is deleted:
            // user subjects and the svc client's own tokens/authz.
            (await conn.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM openiddict_tokens
                WHERE subject = @s OR subject IN
                    (SELECT id FROM users WHERE tenant_id = @t)
                """, new { s = $"svc-{tenant}", t = tenant })).ShouldBe(0);
            (await conn.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM openiddict_authorizations
                WHERE subject = @s OR subject IN
                    (SELECT id FROM users WHERE tenant_id = @t)
                """, new { s = $"svc-{tenant}", t = tenant })).ShouldBe(0);

            // Users are anonymized tombstones — no credential, no PII.
            var liveUsers = await conn.ExecuteScalarAsync<long>(
                """
                SELECT COUNT(*) FROM users WHERE tenant_id = @t
                  AND (email NOT LIKE 'erased-%@erased'
                       OR display_name <> 'erased'
                       OR password_hash IS NOT NULL
                       OR mfa_secret_ref IS NOT NULL
                       OR status <> 'disabled')
                """, new { t = tenant });
            liveUsers.ShouldBe(0);
            (await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM users WHERE tenant_id = @t",
                new { t = tenant })).ShouldBe(2);

            // The audit spine is retained: retired tenant row, rollout
            // history, and the override+erasure events.
            (await conn.ExecuteScalarAsync<string>(
                "SELECT status FROM tenants WHERE id = @t",
                new { t = tenant })).ShouldBe("retired");
            (await conn.ExecuteScalarAsync<long>(
                "SELECT COUNT(*) FROM tenant_migrations WHERE tenant_id = @t",
                new { t = tenant })).ShouldBe(1);
            var actions = (await conn.QueryAsync<string>(
                """
                SELECT action FROM audit_events
                WHERE entity = 'tenant' AND entity_id = @t
                """, new { t = tenant })).ToList();
            actions.ShouldContain("retention_override");
            actions.ShouldContain("tenant_erased");
        }

        // The dead client cannot mint: inert AND gone.
        var mint = await tenantSvc.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = $"svc-{tenant}",
                ["client_secret"] = "marq-secret",
                ["scope"] = "lola-api"
            }));
        mint.IsSuccessStatusCode.ShouldBeFalse();
        (await mint.Content.ReadAsStringAsync()).ShouldContain("invalid_client");
    }

    private static readonly Dictionary<string, string> FullPatch = new()
    {
        ["loan_amount"] = "425000",
        ["loan_purpose"] = "purchase",
        ["amortization_type"] = "fixed",
        ["loan_term_months"] = "360",
        ["party[0].role"] = "borrower",
        ["party[0].first_name"] = "Ada",
        ["party[0].last_name"] = "Lovelace",
        ["party[0].ssn"] = "123456789",
        ["party[0].dob"] = "1985-12-10",
        ["party[0].email"] = "ada@example.com",
        ["party[0].phone"] = "+1-555-0100",
        ["party[0].address[0].street"] = "12 Analytical Way",
        ["party[0].address[0].city"] = "Springfield",
        ["party[0].address[0].state"] = "IL",
        ["party[0].address[0].zip"] = "62701",
        ["party[0].employment[0].employer_name"] = "Acme Engines",
        ["party[0].employment[0].monthly_income"] = "14200",
        ["party[0].employment[0].start_date"] = "2020-01-15",
        ["party[0].employment[0].self_employed"] = "false",
        ["asset[0].asset_type"] = "checking",
        ["asset[0].institution"] = "First Bank",
        ["asset[0].account_number"] = "8844",
        ["asset[0].balance"] = "85000",
        ["liability[0].liability_type"] = "auto",
        ["liability[0].creditor"] = "Car Co",
        ["liability[0].monthly_payment"] = "410",
        ["liability[0].balance"] = "12000",
        ["declaration.intent_to_occupy"] = "yes",
        ["declaration.first_time_buyer"] = "false",
        ["declaration.property_will_be"] = "primary_residence",
        ["subject_property.street"] = "1 Prime Plaza",
        ["subject_property.city"] = "Springfield",
        ["subject_property.state"] = "IL",
        ["subject_property.zip"] = "62701",
        ["subject_property.est_value"] = "560000",
        ["subject_property.property_type"] = "single_family",
        ["subject_property.usage"] = "primary_residence",
        ["demographics.ethnicity"] = "not_provided",
        ["demographics.race"] = "not_provided",
        ["demographics.sex"] = "not_provided",
        ["loan.base_amount"] = "425000",
        ["loan.ltv"] = "75.9",
        ["loan.lien_position"] = "first",
        ["loan.product_type"] = "conventional",
    };

    private static async Task<string> ServiceTokenAsync(HttpClient client)
    {
        var resp = await client.PostAsync("/connect/token", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = "lola-svc",
                ["client_secret"] = "svc-secret-value",
                ["scope"] = "lola-api"
            }));
        resp.EnsureSuccessStatusCode();
        return (await resp.Content.ReadFromJsonAsync<JsonElement>())
            .GetProperty("access_token").GetString()!;
    }
}
