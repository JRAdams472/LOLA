using Dapper;
using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.ControlPlane;
using Lola.Domain.Identity;
using Lola.Infrastructure.Identity;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Lola.Api.Tests;

public class LolaApiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // The signing-key path is read eagerly during service
        // registration — before ConfigureAppConfiguration merges —
        // so it must arrive via a real env var, not an in-memory
        // override (env providers load at CreateBuilder time).
        Environment.SetEnvironmentVariable("OIDC_SIGNING_KEY_PATH",
            Path.Combine(Path.GetTempPath(), "lola-tests", $"oidc-{Guid.NewGuid():N}.json"));
        Environment.SetEnvironmentVariable("OIDC_ENCRYPTION_KEY_PATH",
            Path.Combine(Path.GetTempPath(), "lola-tests", $"oidc-enc-{Guid.NewGuid():N}.json"));

        builder.ConfigureAppConfiguration((_, config) =>
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["CONTROL_DB_PATH"] = Path.Combine(
                    Path.GetTempPath(), "lola-tests", $"control-{Guid.NewGuid():N}.db"),
                ["OIDC_SEED_SVC_SECRET"] = "svc-secret-value",
                ["TENANT_DATA_DIR"] = Path.Combine(
                    Path.GetTempPath(), "lola-tests", "tenants")
            }));

        // Provisioning's crypto steps are proven against real OpenBao in
        // Lola.DataPlane.Tests; here they are stubbed so the API e2e is
        // hermetic. OIDC client provisioning stays real (control-plane).
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ITenantKeyManager>(new FakeKeyManager());
            services.AddSingleton<ITenantMigrationRunner>(new FakeMigrationRunner());
            services.AddSingleton<ITenantBackup>(new FakeTenantBackup());
            services.AddSingleton<ITenantDbProvisioner>(
                new FakeTenantDbProvisioner(Path.Combine(
                    Path.GetTempPath(), "lola-tests", "tenants")));
        });
    }

    /// Seed a tenant + active user into the control-plane DB the app
    /// under test is using (same singleton instance).
    public async Task<LolaUser> SeedUserAsync(
        string tenantId, string email, string password,
        LolaRole role = LolaRole.LoanOfficer,
        string? mfaSecretRef = "vault:dev-mfa-ref-1234",
        string tenantStatus = "active")
    {
        var db = Services.GetRequiredService<ControlPlaneDb>();
        var registry = new TenantRegistry(db);
        await registry.InsertProvisioningAsync(
            tenantId, tenantId, $"Tenant {tenantId}", $"data/tenants/{tenantId}.db");
        await registry.SetStatusAsync(tenantId, tenantStatus);

        var hasher = new Argon2PasswordHasher();
        var user = new LolaUser(
            $"u-{Guid.NewGuid():N}", tenantId, email, "Test User",
            hasher.Hash(password), UserStatus.Active, MfaEnabled: false,
            "2026-10-27T00:00:00Z", mfaSecretRef);
        var users = new UserDirectory(db);
        await users.InsertAsync(user);

        // Group + role + membership so RolesForUserAsync resolves.
        using var conn = db.Open();
        var gid = $"g-{Guid.NewGuid():N}";
        await conn.ExecuteAsync(
            "INSERT INTO groups (id, tenant_id, name) VALUES (@gid, @t, 'g')",
            new { gid, t = tenantId });
        await conn.ExecuteAsync(
            "INSERT INTO group_roles (group_id, role) VALUES (@gid, @r)",
            new { gid, r = role.ToString() });
        await conn.ExecuteAsync(
            "INSERT INTO user_groups (user_id, group_id) VALUES (@uid, @gid)",
            new { uid = user.Id, gid });
        return user;
    }
}
