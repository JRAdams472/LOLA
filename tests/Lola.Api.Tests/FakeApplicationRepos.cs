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
        public string PipelineStep = "POSIntake";
        public string? BorrowerUserId;
        public int Version;
        public string? TridClockStart;
        public string? SubmittedUtc;
        public string CreatedUtc = DateTimeOffset.UtcNow.ToString("O");
        public ApplicationDocument Doc = ApplicationDocument.Empty();
    }

    public ConcurrentDictionary<string, App> Apps { get; } = new();
    public ConcurrentDictionary<string, List<StepEventDto>> Steps { get; } = new();
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

    public Task EnsureTridClockStartAsync(string id, CancellationToken ct = default)
    {
        store.Apps[id].TridClockStart ??= DateTimeOffset.UtcNow.ToString("O");
        return Task.CompletedTask;
    }

    public Task UpdatePipelineStepAsync(string id, string step,
        CancellationToken ct = default)
    {
        store.Apps[id].PipelineStep = step;
        return Task.CompletedTask;
    }

    public Task RecordStepEventAsync(string id, string step, string event_,
        string actor, string? reason, CancellationToken ct = default)
    {
        var list = store.Steps.GetOrAdd(id, _ => []);
        lock (list)
            list.Add(new StepEventDto($"{id}-step-{list.Count + 1}", step,
                event_, actor, reason, DateTimeOffset.UtcNow.ToString("O")));
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
        string id, CancellationToken ct = default)
    {
        if (!store.Steps.TryGetValue(id, out var list))
            return Task.FromResult<IReadOnlyList<StepEventDto>>([]);
        lock (list) return Task.FromResult<IReadOnlyList<StepEventDto>>(list.ToList());
    }

    public Task<ApplicationRecord?> GetAsync(string id, CancellationToken ct = default)
    {
        if (!store.Apps.TryGetValue(id, out var app)) return Task.FromResult<ApplicationRecord?>(null);
        // Clone — handlers mutate the doc (DocMasker) per request.
        return Task.FromResult<ApplicationRecord?>(new ApplicationRecord(
            id, app.Status, app.PipelineStep, app.BorrowerUserId, app.Version,
            app.TridClockStart, app.SubmittedUtc,
            ApplicationDocument.FromObject(app.Doc.CloneRoot())));
    }

    public Task<IReadOnlyList<ApplicationRetentionRow>> RetentionWindowsAsync(
        CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApplicationRetentionRow>>(store.Apps
            .Select(kv => new ApplicationRetentionRow(kv.Key, kv.Value.CreatedUtc))
            .ToList());

    public Task<IReadOnlyList<ApplicationMeta>> ListAsync(CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<ApplicationMeta>>(store.Apps
            .Select(kv => new ApplicationMeta(kv.Key, kv.Value.Status,
                kv.Value.PipelineStep, kv.Value.BorrowerUserId,
                kv.Value.Version, kv.Value.TridClockStart))
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
