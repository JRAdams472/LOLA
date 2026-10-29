using Lola.Domain.Applications;

namespace Lola.Application.Applications;

/// One application row plus its current plaintext document.
/// `SubmittedUtc` non-null marks an immutable snapshot (invariant 6);
/// `TridClockStart` is stamped once (MPE-112).
public sealed record ApplicationRecord(
    string Id, string Status, string PipelineStep, string? BorrowerUserId,
    int CurrentVersion, string? TridClockStart, string? SubmittedUtc,
    ApplicationDocument Doc);

public sealed record ApplicationMeta(
    string Id, string Status, string PipelineStep,
    string? BorrowerUserId, long CurrentVersion, string? TridClockStart);

/// Application store seam (M5 P2) — the DataPlane implementation runs
/// the JSON1 payload + ciphertext/blind columns; handlers only see this.
public interface IApplicationRepository
{
    Task InsertAsync(string id, ApplicationDocument doc, string status,
        string? borrowerUserId, string writtenBy, CancellationToken ct = default);

    /// Append a NEW version — submitted snapshots are never updated.
    Task<int> WriteVersionAsync(string id, ApplicationDocument doc,
        string status, string writtenBy, bool submitted, CancellationToken ct = default);

    /// Lifecycle transition — updates the applications row status only;
    /// version rows stay untouched (invariant 6).
    Task UpdateStatusAsync(string id, string status, CancellationToken ct = default);

    Task<ApplicationRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ApplicationMeta>> ListAsync(CancellationToken ct = default);
}

/// Blind-index equality search — `field` is a catalog trailing key
/// ("ssn", "email", "phone", "first_name", "last_name", "street").
public interface IApplicationPartyRepository
{
    Task<IReadOnlyList<string>> SearchAsync(string field, string value, CancellationToken ct = default);
}
