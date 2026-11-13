using Lola.Application.Applications;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Domain.Applications;
using Lola.Domain.Classification;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-209 meta projection: the repo returns party[0]'s name plaintext
/// and the handler masks it per VisibilityContext (officer-scoped
/// scope = plaintext, absent grant = "•••"+last-4 — invariant 8); the
/// TRID deadline derives from the once-only clock stamp.
public sealed class ListApplicationsTests
{
    private static ApplicationMeta Meta(
        string id = "app-1", string? name = "Ada Lovelace",
        string? stamp = "2026-01-08T10:00:00.000Z", string? borrower = "u-b1") =>
        new(id, "draft", "POSIntake", borrower, 1, stamp, name, null);

    private static IDisposable Scope(IReadOnlySet<PiiClass>? visible = null)
    {
        var tenant = TenantContext.Use(new TenantScope("t1", "x.db", "k"));
        var vis = VisibilityContext.Use(visible ?? new HashSet<PiiClass>());
        return new Both(tenant, vis);
    }

    private sealed class Both(IDisposable a, IDisposable b) : IDisposable
    {
        public void Dispose() { b.Dispose(); a.Dispose(); }
    }

    private sealed class FakeApps(IReadOnlyList<ApplicationMeta> metas)
        : IApplicationRepository
    {
        public Task<IReadOnlyList<ApplicationMeta>> ListAsync(
            CancellationToken ct = default) => Task.FromResult(metas);

        public Task InsertAsync(string id, ApplicationDocument doc, string status,
            string? borrowerUserId, string writtenBy, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> WriteVersionAsync(string id, ApplicationDocument doc,
            string status, string writtenBy, bool submitted, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task EnsureTridClockStartAsync(string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdatePipelineStepAsync(string id, string step, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RecordStepEventAsync(string id, string step, string event_,
            string actor, string? reason, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
            string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationRetentionRow>> RetentionWindowsAsync(
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<ApplicationRecord?> GetAsync(string id, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    [Fact]
    public async Task Officer_scope_sees_borrower_name_plaintext()
    {
        using var _ = Scope(new HashSet<PiiClass> { PiiClass.Name });
        var metas = await ListApplicationsHandler.Handle(
            new ListApplicationsQuery("t1", "u-officer", ActorIsBorrower: false),
            new FakeApps([Meta()]), default);
        metas[0].BorrowerName.ShouldBe("Ada Lovelace");
    }

    [Fact]
    public async Task Absent_name_grant_masks_borrower_name()
    {
        using var _ = Scope(); // empty visibility — masked-by-default
        var metas = await ListApplicationsHandler.Handle(
            new ListApplicationsQuery("t1", "u-svc", ActorIsBorrower: false),
            new FakeApps([Meta()]), default);
        metas[0].BorrowerName.ShouldStartWith("•••");
        metas[0].BorrowerName.ShouldEndWith("lace"); // "•••"+last-4, same shape as DocMasker
    }

    [Fact]
    public async Task Null_name_stays_null_regardless_of_scope()
    {
        using var _ = Scope();
        var metas = await ListApplicationsHandler.Handle(
            new ListApplicationsQuery("t1", "u-svc", ActorIsBorrower: false),
            new FakeApps([Meta(name: null)]), default);
        metas[0].BorrowerName.ShouldBeNull();
    }

    [Fact]
    public async Task Deadline_is_three_business_days_from_stamp()
    {
        // Stamp Thursday 2026-01-08: Fri 9th (1), Sat 10th counts under
        // the Reg Z general rule (2), Mon 12th (3) — Sundays/holidays skip.
        using var _ = Scope();
        var metas = await ListApplicationsHandler.Handle(
            new ListApplicationsQuery("t1", "u-officer", ActorIsBorrower: false),
            new FakeApps([Meta(stamp: "2026-01-08T10:00:00.000Z")]), default);
        metas[0].TridDeadlineUtc.ShouldBe("2026-01-12T00:00:00.000Z");
    }

    [Fact]
    public async Task Unstamped_clock_yields_null_deadline()
    {
        using var _ = Scope();
        var metas = await ListApplicationsHandler.Handle(
            new ListApplicationsQuery("t1", "u-officer", ActorIsBorrower: false),
            new FakeApps([Meta(stamp: null)]), default);
        metas[0].TridDeadlineUtc.ShouldBeNull();
    }

    [Fact]
    public async Task Borrower_scope_sees_own_drafts_only_and_masked()
    {
        using var _ = Scope(); // borrowers hold no plaintext grants
        var metas = await ListApplicationsHandler.Handle(
            new ListApplicationsQuery("t1", "u-b1", ActorIsBorrower: true),
            new FakeApps([Meta(id: "app-mine"), Meta(id: "app-theirs", borrower: "u-b2")]),
            default);
        metas.ShouldHaveSingleItem().Id.ShouldBe("app-mine");
        metas[0].BorrowerName.ShouldStartWith("•••");
    }
}
