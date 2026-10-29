using System.Collections.Concurrent;
using Lola.Application.Applications;
using Lola.Domain.Applications;

namespace Lola.Api.Tests;

/// Shared in-memory store so the fake app + party repos behave like the
/// real seam — search simulates a blind lookup by matching leaf values.
public sealed class FakeApplicationStore
{
    public sealed class App
    {
        public string Status = "draft";
        public string? BorrowerUserId;
        public int Version;
        public string? SubmittedUtc;
        public ApplicationDocument Doc = ApplicationDocument.Empty();
    }

    public ConcurrentDictionary<string, App> Apps { get; } = new();
}

public sealed class FakeApplicationRepository(FakeApplicationStore store)
    : IApplicationRepository
{
    public Task InsertAsync(string id, ApplicationDocument doc, string status,
        string? borrowerUserId, string writtenBy, CancellationToken ct = default)
    {
        store.Apps[id] = new FakeApplicationStore.App
        {
            Status = status,
            BorrowerUserId = borrowerUserId,
            Version = 1,
            Doc = doc
        };
        return Task.CompletedTask;
    }

    public Task<int> WriteVersionAsync(string id, ApplicationDocument doc,
        string status, string writtenBy, bool submitted, CancellationToken ct = default)
    {
        var app = store.Apps[id];
        app.Version++;
        app.Status = status;
        app.Doc = doc;
        if (submitted) app.SubmittedUtc ??= DateTimeOffset.UtcNow.ToString("O");
        return Task.FromResult(app.Version);
    }

    public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default)
    {
        store.Apps[id].Status = status;
        return Task.CompletedTask;
    }

    public Task<ApplicationRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        if (!store.Apps.TryGetValue(id, out var app)) return Task.FromResult<ApplicationRecord?>(null);
        // Clone — handlers mutate the doc (DocMasker) per request.
        return Task.FromResult<ApplicationRecord?>(new ApplicationRecord(
            id, app.Status, "POSIntake", app.BorrowerUserId, app.Version,
            null, app.SubmittedUtc,
            ApplicationDocument.FromObject(app.Doc.CloneRoot())));
    }

    public Task<IReadOnlyList<ApplicationMeta>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApplicationMeta>>(store.Apps
            .Select(kv => new ApplicationMeta(kv.Key, kv.Value.Status,
                "POSIntake", kv.Value.BorrowerUserId, kv.Value.Version, null))
            .ToList());
}

public sealed class FakeApplicationPartyRepository(FakeApplicationStore store)
    : IApplicationPartyRepository
{
    public Task<IReadOnlyList<string>> SearchAsync(string field, string value,
        CancellationToken ct = default)
    {
        var suffix = $".{field}";
        return Task.FromResult<IReadOnlyList<string>>(store.Apps
            .Where(kv => kv.Value.Doc.Leaves().Any(l =>
                l.Path == field || l.Path.EndsWith(suffix, StringComparison.Ordinal)) &&
                kv.Value.Doc.Leaves().First(l =>
                    l.Path == field || l.Path.EndsWith(suffix, StringComparison.Ordinal))
                    .Node.GetValue<string>() == value)
            .Select(kv => kv.Key)
            .ToList());
    }
}
