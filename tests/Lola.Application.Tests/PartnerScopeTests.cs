using Lola.Application.Admin.Borrowers;
using Lola.Application.Applications;
using Lola.Application.Audit;
using Lola.Application.Errors;
using Lola.Application.Events;
using Lola.Application.Identity;
using Lola.Application.Pricing;
using Lola.Application.Tenancy;
using Lola.Application.Vault;
using Lola.Domain.Applications;
using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Application.Tests;

/// M19 P1 (MPE-265) — partner row scope, link/unlink administration,
/// partner invite derivation. Partner reach is the link table and
/// nothing else; every non-linked path is 403.
public sealed class PartnerScopeTests
{
    private static IDisposable Scope()
        => TenantContext.Use(new TenantScope("t1", "x.db", "k"));

    private static ApplicationRecord App(string id = "app-1",
        string? borrower = "u-b1") =>
        new(id, "draft", "POSIntake", borrower, 1, null, null,
            ApplicationDocument.Empty());

    // ---- RequireAppAsync partner branch (via GetApplication) ----

    [Fact]
    public async Task Partner_linked_to_app_reads_it()
    {
        using var _ = Scope();
        var dto = await GetApplicationHandler.Handle(
            new GetApplicationQuery("t1", "app-1", "u-broker",
                ActorScope.Partner),
            new FakeApps(App(), links: [("app-1", "u-broker")]),
            new FakeAudit(), new FakeLocks(), default);
        dto.ShouldNotBeNull().Id.ShouldBe("app-1");
    }

    [Fact]
    public async Task Partner_unlinked_gets_403()
    {
        using var _ = Scope();
        var ex = await Should.ThrowAsync<DomainException>(() =>
            GetApplicationHandler.Handle(
                new GetApplicationQuery("t1", "app-1", "u-broker",
                    ActorScope.Partner),
                new FakeApps(App(), links: [("app-2", "u-broker")]),
                new FakeAudit(), new FakeLocks(), default));
        ex.Status.ShouldBe(403);
    }

    [Fact]
    public async Task Partner_revoked_link_gets_403()
    {
        using var _ = Scope();
        // IsPartnerLinkedAsync only reports active links — a revoked
        // row is indistinguishable from never-linked.
        var ex = await Should.ThrowAsync<DomainException>(() =>
            GetApplicationHandler.Handle(
                new GetApplicationQuery("t1", "app-1", "u-broker",
                    ActorScope.Partner),
                new FakeApps(App()), new FakeAudit(), new FakeLocks(),
                default));
        ex.Status.ShouldBe(403);
    }

    [Fact]
    public async Task Partner_list_is_linked_only()
    {
        using var _ = Scope();
        var metas = new[]
        {
            new ApplicationMeta("app-1", "draft", "POSIntake", "u-b1",
                1, null, null, null),
            new ApplicationMeta("app-2", "draft", "POSIntake", "u-b2",
                1, null, null, null),
        };
        var list = await ListApplicationsHandler.Handle(
            new ListApplicationsQuery("t1", "u-broker", ActorScope.Partner),
            new FakeApps(metas: metas, links: [("app-2", "u-broker")]),
            default);
        list.ShouldHaveSingleItem().Id.ShouldBe("app-2");
    }

    [Fact]
    public async Task Partner_cannot_patch_submit_or_originate()
    {
        using var _ = Scope();
        var apps = new FakeApps(App(), links: [("app-1", "u-broker")]);
        (await Should.ThrowAsync<DomainException>(() =>
            UpdateDraftHandler.Handle(
                new UpdateDraftCommand("t1", "app-1",
                    new Dictionary<string, string> { ["loan_amount"] = "1" },
                    "u-broker", ActorScope.Partner),
                apps, new FakeAudit(), new FakeLocks(), default)))
            .Status.ShouldBe(403);
        (await Should.ThrowAsync<DomainException>(() =>
            CreateDraftHandler.Handle(
                new CreateDraftCommand("t1", null, "u-broker",
                    ActorScope.Partner), apps, default)))
            .Status.ShouldBe(403);
    }

    // ---- Vault own-upload filtering ----

    [Fact]
    public async Task Partner_uploads_list_is_own_only()
    {
        using var _ = Scope();
        var docs = new[]
        {
            Doc("d-mine", "u-broker"), Doc("d-borrower", "u-b1"),
            Doc("d-officer", "u-officer")
        };
        var list = await GetApplicationUploadsHandler.Handle(
            new GetApplicationUploadsQuery("t1", "app-1", "u-broker",
                ActorScope.Partner),
            new FakeApps(App(), links: [("app-1", "u-broker")]),
            new FakeVault(docs), default);
        list.ShouldHaveSingleItem().VaultDocId.ShouldBe("d-mine");
    }

    [Fact]
    public async Task Partner_cannot_read_another_uploaders_doc()
    {
        using var _ = Scope();
        var ex = await Should.ThrowAsync<DomainException>(() =>
            GetVaultDocHandler.Handle(
                new GetVaultDocQuery("t1", "app-1", "d-borrower",
                    "u-broker", ActorScope.Partner),
                new FakeApps(App(), links: [("app-1", "u-broker")]),
                new FakeVault([Doc("d-borrower", "u-b1")]), default));
        ex.Status.ShouldBe(403);
    }

    // ---- Link / unlink administration ----

    [Fact]
    public async Task Link_writes_row_event_and_audit()
    {
        using var _ = Scope();
        var links = new FakeLinks();
        var audit = new FakeAudit();
        var events = new FakeEventLog();
        var dto = await LinkPartnerHandler.Handle(
            new LinkPartnerCommand("t1", "app-1", "u-broker", "u-officer"),
            new FakeApps(App()), links,
            new FakeUsers(id: "u-broker", role: LolaRole.Broker),
            audit, events, default);
        dto.Role.ShouldBe("Broker");
        links.Store.ShouldHaveSingleItem().PartnerUserId
            .ShouldBe("u-broker");
        events.Drafts.ShouldHaveSingleItem().EventKind
            .ShouldBe(LoanEventKind.PartnerLinked);
        audit.Rows.ShouldHaveSingleItem().Action
            .ShouldBe("partner_linked");
    }

    [Fact]
    public async Task Link_non_partner_user_is_400()
    {
        using var _ = Scope();
        var ex = await Should.ThrowAsync<DomainException>(() =>
            LinkPartnerHandler.Handle(
                new LinkPartnerCommand("t1", "app-1", "u-officer",
                    "u-admin"),
                new FakeApps(App()), new FakeLinks(),
                new FakeUsers(id: "u-officer", role: LolaRole.LoanOfficer),
                new FakeAudit(), new FakeEventLog(), default));
        ex.Status.ShouldBe(400);
    }

    [Fact]
    public async Task Unlink_revokes_and_audits_missing_link_404s()
    {
        using var _ = Scope();
        var links = new FakeLinks();
        links.Store.Add(new PartnerLink("app-1", "u-broker", "Broker",
            "u-admin", "2026-11-20T00:00:00Z", null));
        var audit = new FakeAudit();
        var events = new FakeEventLog();
        (await UnlinkPartnerHandler.Handle(
            new UnlinkPartnerCommand("t1", "app-1", "u-broker",
                "u-officer"),
            new FakeApps(App()), links, audit, events, default))
            .ShouldBeTrue();
        links.Store[0].RevokedUtc.ShouldNotBeNull();
        events.Drafts.ShouldHaveSingleItem().EventKind
            .ShouldBe(LoanEventKind.PartnerRevoked);
        audit.Rows.ShouldHaveSingleItem().Action
            .ShouldBe("partner_revoked");

        var ex = await Should.ThrowAsync<DomainException>(() =>
            UnlinkPartnerHandler.Handle(
                new UnlinkPartnerCommand("t1", "app-1", "u-broker",
                    "u-officer"),
                new FakeApps(App()), links, audit, events, default));
        ex.Status.ShouldBe(404);
    }

    // ---- Partner invite ----

    [Fact]
    public async Task Partner_invite_carries_role_and_org()
    {
        using var _ = Scope();
        var invites = new FakeInvites();
        var result = await CreateInviteHandler.Handle(
            new CreateInviteCommand("t1", "broker@tpo.example",
                "u-admin", LolaRole.Broker, "TPO Originations LLC"),
            invites, new FakeUsers(), new FakeDelivery(), default);
        result.InviteUrl.ShouldNotBeNull();
        var row = invites.Store.ShouldHaveSingleItem();
        row.Role.ShouldBe(LolaRole.Broker);
        row.OrgName.ShouldBe("TPO Originations LLC");
    }

    [Fact]
    public async Task Staff_role_invite_is_rejected()
    {
        using var _ = Scope();
        var ex = await Should.ThrowAsync<DomainException>(() =>
            CreateInviteHandler.Handle(
                new CreateInviteCommand("t1", "mole@x.example",
                    "u-admin", LolaRole.TenantAdmin),
                new FakeInvites(), new FakeUsers(), new FakeDelivery(),
                default));
        ex.Status.ShouldBe(400);
    }

    [Theory]
    [InlineData(LolaRole.Broker, "brokers")]
    [InlineData(LolaRole.Realtor, "realtors")]
    [InlineData(LolaRole.Borrower, "borrowers")]
    public async Task Accept_derives_group_and_role_from_invite(
        LolaRole role, string groupName)
    {
        using var _ = Scope();
        var invites = new FakeInvites();
        var groups = new FakeGroups();
        var users = new FakeUsers(groups);
        var token = "tok-" + Guid.NewGuid().ToString("N");
        invites.Seed(role, token, "TPO Org");

        var result = await AcceptInviteHandler.Handle(
            new AcceptInviteCommand(token, "Partner One",
                "correct horse battery staple"),
            invites, users, groups, new FakeHasher(), default);
        result.Status.ShouldBe("active");
        var grp = groups.Store.ShouldHaveSingleItem();
        grp.Name.ShouldBe(groupName);
        grp.Roles.ShouldBe([role]);
        groups.Members.ShouldContain((grp.Id, result.UserId));
        var user = users.Store.ShouldHaveSingleItem();
        user.OrgName.ShouldBe("TPO Org");
        users.RolesFor(result.UserId).ShouldBe([role]);
    }

    // ---- fakes ----

    private static VaultDocRecord Doc(string id, string actor) =>
        new(id, "app-1", "w2", "f.pdf", "application/pdf", 10,
            "sha", "w", "minio", "k", "clean", null, actor,
            "2026-11-20T00:00:00Z", null, "h");

    private sealed class FakeApps(
        ApplicationRecord? record = null,
        IEnumerable<ApplicationMeta>? metas = null,
        IEnumerable<(string AppId, string UserId)>? links = null)
        : IApplicationRepository
    {
        private readonly HashSet<(string, string)> _links =
            links?.ToHashSet() ?? [];

        public Task<ApplicationRecord?> GetAsync(string id,
            CancellationToken ct = default)
            => Task.FromResult(record?.Id == id ? record : null);
        public Task<IReadOnlyList<ApplicationMeta>> ListAsync(
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<ApplicationMeta>>(
                (metas ?? []).ToList());
        public Task<bool> IsPartnerLinkedAsync(string id,
            string partnerUserId, CancellationToken ct = default)
            => Task.FromResult(_links.Contains((id, partnerUserId)));
        public Task<IReadOnlyList<string>> LinkedAppIdsForPartnerAsync(
            string partnerUserId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(_links
                .Where(l => l.Item2 == partnerUserId)
                .Select(l => l.Item1).ToList());
        public Task InsertAsync(string id, ApplicationDocument doc,
            string status, string? borrowerUserId, string writtenBy,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> WriteVersionAsync(string id,
            ApplicationDocument doc, string status, string writtenBy,
            bool submitted, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<(int Version, ApplicationDocument Doc)> PatchDraftAsync(
            string id, IReadOnlyDictionary<string, string> patch,
            string writtenBy, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdateStatusAsync(string id, string status,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task EnsureTridClockStartAsync(string id,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task UpdatePipelineStepAsync(string id, string step,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RecordStepEventAsync(string id, string step,
            string event_, string actor, string? reason,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
            string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<ApplicationRetentionRow>>
            RetentionWindowsAsync(CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeLinks : IApplicationPartnerRepository
    {
        public List<PartnerLink> Store { get; } = [];
        public Task LinkAsync(PartnerLink link,
            CancellationToken ct = default)
        { Store.Add(link); return Task.CompletedTask; }
        public Task<bool> RevokeAsync(string appId, string partnerUserId,
            string revokedUtc, CancellationToken ct = default)
        {
            var i = Store.FindIndex(l => l.AppId == appId
                && l.PartnerUserId == partnerUserId
                && l.RevokedUtc is null);
            if (i < 0) return Task.FromResult(false);
            Store[i] = Store[i] with { RevokedUtc = revokedUtc };
            return Task.FromResult(true);
        }
        public Task<IReadOnlyList<PartnerLink>> ListForAppAsync(
            string appId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<PartnerLink>>(Store);
    }

    private sealed class FakeUsers(
        FakeGroups? groups = null,
        string? id = null, LolaRole role = LolaRole.Broker) : IUserDirectory
    {
        public List<LolaUser> Store { get; } = [];

        public IReadOnlyList<LolaRole> RolesFor(string userId)
        {
            if (groups is null || !groups.Members.Any(
                    m => m.UserId == userId))
                return [];
            var ids = groups.Members.Where(m => m.UserId == userId)
                .Select(m => m.GroupId).ToHashSet();
            return groups.Store.Where(g => ids.Contains(g.Id))
                .SelectMany(g => g.Roles).ToList();
        }
        public Task<LolaUser?> FindByIdAsync(string userId,
            CancellationToken ct = default)
            => Task.FromResult(Store.FirstOrDefault(u => u.Id == userId)
                ?? (id == userId
                    ? new LolaUser(userId, "t1", "p@x", "P", null,
                        UserStatus.Active, false, "now")
                    : null));
        public Task<LolaUser?> FindByEmailAsync(string email,
            CancellationToken ct = default)
            => Task.FromResult<LolaUser?>(null);
        public Task<LolaUser?> FindByEmailInTenantAsync(string tenantId,
            string email, CancellationToken ct = default)
            => Task.FromResult<LolaUser?>(null);
        public Task<IReadOnlyList<LolaUser>> ListByTenantAsync(
            string tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LolaUser>>(Store);
        public Task<IReadOnlyList<LolaRole>> RolesForUserAsync(
            string userId, CancellationToken ct = default)
        {
            var derived = RolesFor(userId);
            return Task.FromResult<IReadOnlyList<LolaRole>>(
                derived.Count > 0 ? derived
                    : id == userId ? [role] : []);
        }
        public Task InsertAsync(LolaUser user,
            CancellationToken ct = default)
        {
            Store.Add(user);
            return Task.CompletedTask;
        }
        public Task UpdatePasswordAsync(string userId,
            string passwordHash, string status,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task SetStatusAsync(string userId, string status,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeGroups : IGroupDirectory
    {
        public List<LolaGroup> Store { get; } = [];
        public List<(string GroupId, string UserId)> Members { get; } = [];
        public Task<IReadOnlyList<LolaGroup>> ListByTenantAsync(
            string tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LolaGroup>>(Store);
        public Task<LolaGroup?> FindByIdAsync(string id,
            CancellationToken ct = default)
            => Task.FromResult(Store.FirstOrDefault(g => g.Id == id));
        public Task<int> MemberCountAsync(string groupId,
            CancellationToken ct = default)
            => Task.FromResult(Members.Count(m => m.GroupId == groupId));
        public Task InsertAsync(LolaGroup group,
            CancellationToken ct = default)
        { Store.Add(group); return Task.CompletedTask; }
        public Task UpdateAsync(string id, string? name,
            string? description, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task SetRolesAsync(string groupId,
            IReadOnlyCollection<LolaRole> roles,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task DeleteAsync(string id, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task AddMemberAsync(string groupId, string userId,
            CancellationToken ct = default)
        { Members.Add((groupId, userId)); return Task.CompletedTask; }
        public Task RemoveMemberAsync(string groupId, string userId,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<string>> MemberIdsAsync(string groupId,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeInvites : IInviteDirectory
    {
        public List<InviteRecord> Store { get; } = [];
        public void Seed(LolaRole role, string token, string? org)
        {
            var hash = Convert.ToHexString(
                System.Security.Cryptography.SHA256.HashData(
                    System.Text.Encoding.ASCII.GetBytes(token)))
                .ToLowerInvariant();
            Store.Add(new InviteRecord("i-1", "t1", "p@x", role, hash,
                DateTimeOffset.UtcNow.AddHours(1).ToString("O"), null,
                "u-admin", org));
        }
        public Task InsertAsync(InviteRecord invite,
            CancellationToken ct = default)
        { Store.Add(invite); return Task.CompletedTask; }
        public Task<InviteRecord?> FindByTokenHashAsync(string tokenHash,
            CancellationToken ct = default)
            => Task.FromResult(Store.FirstOrDefault(
                i => i.TokenHash == tokenHash));
        public Task<bool> MarkUsedAsync(string inviteId, string usedUtc,
            CancellationToken ct = default)
            => Task.FromResult(true);
    }

    private sealed class FakeHasher : IPasswordHasher
    {
        public string Hash(string password) => $"h:{password}";
        public bool Verify(string password, string passwordHash)
            => passwordHash == $"h:{password}";
    }

    private sealed class FakeDelivery : IInviteDelivery
    {
        public Task<Uri> DeliverAsync(string email, string tenantId,
            string token, CancellationToken ct = default)
            => Task.FromResult(new Uri(
                $"https://x.test/accept?token={token}"));
    }

    private sealed class FakeAudit : ITenantAuditLog
    {
        public List<(string Action, string Entity)> Rows { get; } = [];
        public Task WriteAsync(string actor, string action,
            string entity, string? entityId,
            IReadOnlyList<string> fieldClasses, string? reason,
            CancellationToken ct = default)
        { Rows.Add((action, entity)); return Task.CompletedTask; }
        public Task<AuditVerifyResult> VerifyChainAsync(
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<AuditEvent>> QueryAsync(
            AuditFilter filter, CancellationToken ct = default)
            => throw new NotImplementedException();
    }

    private sealed class FakeLocks : IRateLockRepository
    {
        public Task InsertAsync(RateLockRecord row,
            CancellationToken ct = default)
            => Task.CompletedTask;
        public Task<RateLockRecord?> GetActiveAsync(string appId,
            CancellationToken ct = default)
            => Task.FromResult<RateLockRecord?>(null);
        public Task<IReadOnlyList<RateLockRecord>> ListAsync(
            string appId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<RateLockRecord>>([]);
        public Task UnlockAsync(string lockId, string reason,
            string unlockedBy, string unlockedUtc,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }

    private sealed class FakeVault(
        IEnumerable<VaultDocRecord> docs) : IVaultDocRepository
    {
        public Task<VaultDocRecord?> GetAsync(string vaultDocId,
            CancellationToken ct = default)
            => Task.FromResult(docs.FirstOrDefault(
                d => d.VaultDocId == vaultDocId));
        public Task<IReadOnlyList<VaultDocRecord>> ListAsync(
            string appId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<VaultDocRecord>>(
                docs.ToList());
        public Task<VaultEnqueueResult> EnqueueAsync(VaultDocRecord doc,
            string outboxId, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<IReadOnlyList<VaultScanOutboxRow>> ListDueScansAsync(
            string now, int limit, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkScanAttemptAsync(string outboxId, long attempts,
            string? nextRetryUtc, CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkScanResultAsync(string outboxId,
            string vaultDocId, string status, string? error,
            string scannedUtc, bool deadLetter,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkScanningAsync(string outboxId, string now,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task CompleteScanAsync(string outboxId, string vaultDocId,
            string scannedUtc, string destKey,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkInfectedAsync(string outboxId, string vaultDocId,
            string virusName, string scannedUtc,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task MarkUnavailableAsync(string outboxId,
            string vaultDocId, string scannedUtc,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task RescheduleScanAsync(string outboxId, long attempts,
            string lastError, string nextRetryUtc,
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task<int> ResetInflightScansAsync(
            CancellationToken ct = default)
            => throw new NotImplementedException();
        public Task SaveClassificationAsync(string vaultDocId,
            string classificationJson, string classifiedUtc,
            CancellationToken ct = default)
            => throw new NotImplementedException();
    }
}
