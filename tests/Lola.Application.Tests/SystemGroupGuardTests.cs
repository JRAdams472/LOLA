using Lola.Application.Admin.Borrowers;
using Lola.Application.Admin.Groups;
using Lola.Application.Errors;
using Lola.Application.Identity;
using Lola.Application.Tenancy;
using Lola.Domain.Identity;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-273 handler-ingress protection: reserved system-group names
/// and protected identities reject ordinary admin mutation, while
/// explicit membership administration stays available.
public class SystemGroupGuardTests
{
    private static IDisposable Scope()
        => TenantContext.Use(new TenantScope("t1", "x.db", "k"));

    private static LolaGroup SystemGroup()
        => new("g-sys", "t1", "brokers", "Broker partners (TPO)",
            [LolaRole.Broker], "broker");

    [Theory]
    [InlineData("borrowers")]
    [InlineData("brokers")]
    [InlineData("realtors")]
    public async Task Create_rejects_reserved_names(string name)
    {
        using var _ = Scope();
        var ex = await Should.ThrowAsync<DomainException>(() =>
            CreateGroupHandler.Handle(
                new CreateGroupCommand("t1", name, null, [LolaRole.Borrower]),
                new FakeGroups(), default));
        ex.Status.ShouldBe(400);
    }

    [Fact]
    public async Task Create_allows_nonreserved_name()
    {
        using var _ = Scope();
        var dto = await CreateGroupHandler.Handle(
            new CreateGroupCommand("t1", "senior-officers", null,
                [LolaRole.LoanOfficer]), new FakeGroups(), default);
        dto.Name.ShouldBe("senior-officers");
    }

    [Fact]
    public async Task Update_rejects_protected_group()
    {
        using var _ = Scope();
        var groups = new FakeGroups(SystemGroup());
        var ex = await Should.ThrowAsync<DomainException>(() =>
            UpdateGroupHandler.Handle(
                new UpdateGroupCommand("t1", "g-sys", "renamed", null, null),
                groups, default));
        ex.Status.ShouldBe(403);
        groups.Store[0].Name.ShouldBe("brokers");
    }

    [Fact]
    public async Task Update_rejects_rename_into_reserved_name()
    {
        using var _ = Scope();
        var groups = new FakeGroups(new LolaGroup("g-a", "t1", "custom",
            null, [LolaRole.Borrower]));
        var ex = await Should.ThrowAsync<DomainException>(() =>
            UpdateGroupHandler.Handle(
                new UpdateGroupCommand("t1", "g-a", "borrowers", null, null),
                groups, default));
        ex.Status.ShouldBe(400);
    }

    [Fact]
    public async Task Delete_rejects_protected_group()
    {
        using var _ = Scope();
        var groups = new FakeGroups(SystemGroup());
        var ex = await Should.ThrowAsync<DomainException>(() =>
            DeleteGroupHandler.Handle(
                new DeleteGroupCommand("t1", "g-sys"), groups, default));
        ex.Status.ShouldBe(403);
        groups.Store.ShouldHaveSingleItem();
    }

    [Fact]
    public async Task Membership_admin_on_system_group_stays_explicit_and_allowed()
    {
        // Invite-derived grants and admin membership management are
        // separate operations — protecting identity/roles must not
        // remove an admin's ability to grant Borrower to a user they
        // already administer.
        using var _ = Scope();
        var groups = new FakeGroups(SystemGroup());
        var dto = await AddUserToGroupHandler.Handle(
            new AddUserToGroupCommand("t1", "g-sys", "u-1"),
            groups, new FakeUsers(), default);
        dto.Id.ShouldBe("g-sys");
        groups.Members.ShouldContain(("g-sys", "u-1"));
    }

    // ---- fakes ----

    private sealed class FakeGroups(params LolaGroup[] seeded)
        : IGroupDirectory
    {
        public List<LolaGroup> Store { get; } = [.. seeded];
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
        {
            var i = Store.FindIndex(g => g.Id == id);
            Store[i] = Store[i] with
            {
                Name = name ?? Store[i].Name,
                Description = description ?? Store[i].Description,
            };
            return Task.CompletedTask;
        }
        public Task SetRolesAsync(string groupId,
            IReadOnlyCollection<LolaRole> roles,
            CancellationToken ct = default)
        {
            var i = Store.FindIndex(g => g.Id == groupId);
            Store[i] = Store[i] with { Roles = roles.ToList() };
            return Task.CompletedTask;
        }
        public Task DeleteAsync(string id, CancellationToken ct = default)
        { Store.RemoveAll(g => g.Id == id); return Task.CompletedTask; }
        public Task AddMemberAsync(string groupId, string userId,
            CancellationToken ct = default)
        { Members.Add((groupId, userId)); return Task.CompletedTask; }
        public Task RemoveMemberAsync(string groupId, string userId,
            CancellationToken ct = default)
        {
            Members.RemoveAll(m => m.GroupId == groupId && m.UserId == userId);
            return Task.CompletedTask;
        }
        public Task<IReadOnlyList<string>> MemberIdsAsync(string groupId,
            CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<string>>(
                Members.Where(m => m.GroupId == groupId)
                    .Select(m => m.UserId).ToList());
    }

    private sealed class FakeUsers : IUserDirectory
    {
        public Task<LolaUser?> FindByIdAsync(string id,
            CancellationToken ct = default)
            => Task.FromResult<LolaUser?>(new LolaUser(id, "t1", "p@x",
                "P", null, UserStatus.Active, false, "now"));
        public Task<LolaUser?> FindByEmailAsync(string email,
            CancellationToken ct = default)
            => Task.FromResult<LolaUser?>(null);
        public Task<LolaUser?> FindByEmailInTenantAsync(string tenantId,
            string email, CancellationToken ct = default)
            => Task.FromResult<LolaUser?>(null);
        public Task<IReadOnlyList<LolaUser>> ListByTenantAsync(
            string tenantId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LolaUser>>([]);
        public Task<IReadOnlyList<LolaRole>> RolesForUserAsync(
            string userId, CancellationToken ct = default)
            => Task.FromResult<IReadOnlyList<LolaRole>>([]);
        public Task InsertAsync(LolaUser user,
            CancellationToken ct = default)
            => Task.CompletedTask;
        public Task UpdatePasswordAsync(string userId,
            string passwordHash, string status,
            CancellationToken ct = default)
            => Task.CompletedTask;
        public Task SetStatusAsync(string userId, string status,
            CancellationToken ct = default)
            => Task.CompletedTask;
    }
}
