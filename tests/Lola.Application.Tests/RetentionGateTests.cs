using Lola.Application.Admin.Tenants;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-118: the Reg B gate on EraseTenantHandler, proven against an
/// injectable TimeProvider — no wall-clock dependence anywhere.
public sealed class RetentionGateTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Inside_window_delete_is_refused_with_clear_date()
    {
        var (handler, _, _, audit) = Fixture(
            [new ApplicationRetentionRow("app-1", "2026-05-01T00:00:00.000Z")]);
        var ex = await Should.ThrowAsync<DomainException>(handler(
            new EraseTenantCommand("t1", Force: false, null, "ops")));
        ex.Status.ShouldBe(409);
        ex.Message.ShouldContain("2028-06-01"); // created + 25 months
        audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Force_without_reason_is_400_and_erases_nothing()
    {
        var (handler, _, erased, audit) = Fixture(
            [new ApplicationRetentionRow("app-1", "2026-05-01T00:00:00.000Z")]);
        var ex = await Should.ThrowAsync<DomainException>(handler(
            new EraseTenantCommand("t1", Force: true, null, "ops")));
        ex.Status.ShouldBe(400);
        erased.ShouldBeEmpty();
        audit.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task Force_with_reason_erases_and_audits_override()
    {
        var (handler, _, erased, audit) = Fixture(
            [new ApplicationRetentionRow("app-1", "2026-05-01T00:00:00.000Z")]);
        var result = await handler(
            new EraseTenantCommand("t1", Force: true, "court order", "ops"));
        result.TenantId.ShouldBe("t1");
        erased.ShouldBe(["t1"]);
        audit.Events.Count.ShouldBe(2);
        var ev = audit.Events[0];
        ev.Action.ShouldBe("retention_override");
        ev.Entity.ShouldBe("tenant");
        ev.EntityId.ShouldBe("t1");
        ev.Actor.ShouldBe("ops");
        ev.Reason.ShouldBe("court order");
        // The erasure itself is attributed in the control-plane log —
        // the tenant file's own chain is shredded with the data.
        audit.Events[1].Action.ShouldBe("tenant_erased");
    }

    [Fact]
    public async Task Past_window_and_empty_tenants_erase_without_override()
    {
        // App created 26 months ago — window closed before `Now`.
        var (handler, _, erased, audit) = Fixture(
            [new ApplicationRetentionRow("app-1", "2024-04-01T00:00:00.000Z")]);
        (await handler(new EraseTenantCommand("t1", false, null, "ops")))
            .TenantId.ShouldBe("t1");
        erased.ShouldBe(["t1"]);

        var (empty, _, emptyErased, emptyAudit) = Fixture([]);
        (await empty(new EraseTenantCommand("t1", false, null, "ops")))
            .TenantId.ShouldBe("t1");
        emptyErased.ShouldBe(["t1"]);
        // No override needed — but the erase itself is still attributed.
        emptyAudit.Events.Select(e => e.Action).ShouldBe(["tenant_erased"]);
    }

    private static (Func<EraseTenantCommand, Task<TenantErasureResult>> Run,
        FakeRegistry Registry, List<string> Erased, FakeAudit Audit) Fixture(
            IReadOnlyList<ApplicationRetentionRow> windows)
    {
        var registry = new FakeRegistry();
        var apps = new FakeApps(windows);
        var eraser = new FakeEraser();
        var audit = new FakeAudit();
        var clock = new FakeClock(Now);
        return (c => EraseTenantHandler.Handle(
                    c, registry, apps, eraser, audit, clock,
                    CancellationToken.None),
                registry, eraser.Erased, audit);
    }

    private static TenantRecord Tenant =>
        new("t1", "t1", "Tenant", "active", "data/t1.db",
            "vault:v1:wrapped", "2020-01-01T00:00:00Z");

    private sealed class FakeRegistry : ITenantRegistry
    {
        public Task<TenantRecord?> FindByIdAsync(string id, CancellationToken ct) =>
            Task.FromResult<TenantRecord?>(id == Tenant.Id ? Tenant : null);
        public Task<TenantRecord?> FindBySlugAsync(string slug, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<TenantRecord>> ListAsync(CancellationToken ct) =>
            throw new NotImplementedException();
        public Task InsertProvisioningAsync(string id, string slug, string displayName, string dbPath, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task SetDbKeyAsync(string tenantId, string dbKeyWrapped, CancellationToken ct) =>
            throw new NotImplementedException();
        public Task SetStatusAsync(string tenantId, string status, CancellationToken ct) =>
            Task.CompletedTask;
    }

    private sealed class FakeApps(IReadOnlyList<ApplicationRetentionRow> rows)
        : IApplicationRepository
    {
        public Task<IReadOnlyList<ApplicationRetentionRow>> RetentionWindowsAsync(
            CancellationToken ct = default) => Task.FromResult(rows);
        public Task InsertAsync(string id, Lola.Domain.Applications.ApplicationDocument doc, string status, string? borrowerUserId, string writtenBy, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<int> WriteVersionAsync(string id, Lola.Domain.Applications.ApplicationDocument doc, string status, string writtenBy, bool submitted, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => throw new NotImplementedException();
        public Task EnsureTridClockStartAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task UpdatePipelineStepAsync(string id, string step, CancellationToken ct = default) => throw new NotImplementedException();
        public Task RecordStepEventAsync(string id, string step, string event_, string actor, string? reason, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<ApplicationRecord?> GetAsync(string id, CancellationToken ct = default) => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationMeta>> ListAsync(CancellationToken ct = default) => throw new NotImplementedException();
    }

    private sealed class FakeEraser : ITenantEraser
    {
        public List<string> Erased { get; } = [];
        public Task<TenantErasureResult> EraseAsync(
            TenantRecord tenant, CancellationToken ct = default)
        {
            Erased.Add(tenant.Id);
            return Task.FromResult(new TenantErasureResult(tenant.Id, 3, []));
        }
    }

    private sealed class FakeAudit : IControlPlaneAuditLog
    {
        public List<(string Actor, string Action, string Entity,
            string? EntityId, string? Reason)> Events
        { get; } = [];
        public Task WriteAsync(string actor, string action, string entity,
            string? entityId, IReadOnlyList<string> fieldClasses,
            string? reason, CancellationToken ct = default)
        {
            Events.Add((actor, action, entity, entityId, reason));
            return Task.CompletedTask;
        }
        public Task<AuditVerifyResult> VerifyChainAsync(CancellationToken ct = default) =>
            throw new NotImplementedException();
        public Task<IReadOnlyList<AuditEvent>> QueryAsync(AuditFilter filter, CancellationToken ct = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
