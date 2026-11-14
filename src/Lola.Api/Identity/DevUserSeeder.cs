using Lola.Application.Identity;
using Lola.Domain.Identity;

namespace Lola.Api.Identity;

/// Dev-stack first-user seeder (MPE-208). Humans reach the console only
/// through interactive login, and users are created BY a TenantAdmin —
/// so a fresh stack has no first human. When DEV_SEED_* env is present
/// this hosted service provisions the demo tenant (re-entrant pipeline),
/// then idempotently creates an admin and/or officer user with group
/// membership. Absent env = no-op; this exists for dev/e2e only.
public sealed class DevUserSeeder(
    IServiceProvider services, IConfiguration config,
    ILogger<DevUserSeeder> log) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var tenantId = config["DEV_SEED_TENANT"];
        var adminEmail = config["DEV_SEED_ADMIN_EMAIL"];
        var adminPassword = config["DEV_SEED_ADMIN_PASSWORD"];
        if (string.IsNullOrWhiteSpace(tenantId)
            || string.IsNullOrWhiteSpace(adminEmail)
            || string.IsNullOrWhiteSpace(adminPassword))
            return;

        await using var scope = services.CreateAsyncScope();
        var registry = scope.ServiceProvider.GetRequiredService<ITenantRegistry>();
        var provisioner = scope.ServiceProvider.GetRequiredService<ITenantProvisioner>();
        var users = scope.ServiceProvider.GetRequiredService<IUserDirectory>();
        var groups = scope.ServiceProvider.GetRequiredService<IGroupDirectory>();
        var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();

        // The pipeline is re-entrant — it skips completed steps and
        // resumes partial ones, so older dev tenants pick up newer
        // provisioner steps (e.g. the ISPPr binding) on every boot.
        await provisioner.ProvisionAsync(new ProvisionRequest(
            tenantId,
            config["DEV_SEED_TENANT_SLUG"] ?? tenantId,
            config["DEV_SEED_TENANT_NAME"] ?? $"Demo {tenantId}"),
            cancellationToken);
        log.LogInformation("dev-seed: provisioned tenant {Tenant}", tenantId);

        var adminGroup = await EnsureGroup(groups, tenantId,
            "tenant-admins", [LolaRole.TenantAdmin], cancellationToken);
        var officerGroup = await EnsureGroup(groups, tenantId,
            "officers", [LolaRole.LoanOfficer], cancellationToken);

        await EnsureUser(users, groups, hasher, tenantId, adminEmail,
            config["DEV_SEED_ADMIN_NAME"] ?? "Dev Admin", adminPassword,
            adminGroup, log, cancellationToken);

        var officerEmail = config["DEV_SEED_OFFICER_EMAIL"];
        var officerPassword = config["DEV_SEED_OFFICER_PASSWORD"];
        if (!string.IsNullOrWhiteSpace(officerEmail)
            && !string.IsNullOrWhiteSpace(officerPassword))
            await EnsureUser(users, groups, hasher, tenantId, officerEmail,
                config["DEV_SEED_OFFICER_NAME"] ?? "Dev Officer", officerPassword,
                officerGroup, log, cancellationToken);
    }

    private static async Task<LolaGroup> EnsureGroup(
        IGroupDirectory groups, string tenantId, string name,
        LolaRole[] roles, CancellationToken ct)
    {
        var existing = (await groups.ListByTenantAsync(tenantId, ct))
            .FirstOrDefault(g => g.Name == name);
        if (existing is not null) return existing;
        var group = new LolaGroup(
            $"g-{Guid.NewGuid():N}", tenantId, name, "dev-seeded", roles);
        await groups.InsertAsync(group, ct);
        return group;
    }

    private static async Task EnsureUser(
        IUserDirectory users, IGroupDirectory groups, IPasswordHasher hasher,
        string tenantId, string email, string displayName, string password,
        LolaGroup group, ILogger log, CancellationToken ct)
    {
        var user = await users.FindByEmailInTenantAsync(tenantId, email, ct);
        if (user is null)
        {
            user = new LolaUser(
                $"u-{Guid.NewGuid():N}", tenantId, email, displayName,
                hasher.Hash(password), UserStatus.Active, MfaEnabled: false,
                DateTimeOffset.UtcNow.ToString("O"));
            await users.InsertAsync(user, ct);
            log.LogInformation("dev-seed: created {Email} in {Tenant}", email, tenantId);
        }
        // INSERT OR IGNORE — re-runs are no-ops.
        await groups.AddMemberAsync(group.Id, user.Id, ct);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
