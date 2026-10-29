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

/// Append-only pipeline step event (MPE-112) — every named-step
/// entry or seam-keyed attempt, attributed to an actor.
public sealed record StepEventDto(
    string Id, string Step, string Event, string Actor,
    string? Reason, string CreatedUtc);

/// Retention input (MPE-118) — id + created_utc only; no payload.
public sealed record ApplicationRetentionRow(string Id, string CreatedUtc);

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

    /// Stamp `trid_clock_start` — the WHERE-clause keeps it once-only:
    /// a second call on an already-stamped row is a no-op (immutable).
    Task EnsureTridClockStartAsync(string id, CancellationToken ct = default);

    /// Named-step move — pipeline position only (workflow seam).
    Task UpdatePipelineStepAsync(string id, string step, CancellationToken ct = default);

    /// Append a step event — never updates or deletes (audit, invariant 4).
    Task RecordStepEventAsync(string id, string step, string event_,
        string actor, string? reason, CancellationToken ct = default);

    Task<IReadOnlyList<StepEventDto>> ListStepEventsAsync(
        string id, CancellationToken ct = default);

    /// Per-application created_utc — the Reg B retention engine reads
    /// windows from this; it deliberately returns no document payload.
    Task<IReadOnlyList<ApplicationRetentionRow>> RetentionWindowsAsync(
        CancellationToken ct = default);

    Task<ApplicationRecord?> GetAsync(string id, CancellationToken ct = default);
    Task<IReadOnlyList<ApplicationMeta>> ListAsync(CancellationToken ct = default);
}

/// Blind-index equality search — `field` is a catalog trailing key
/// ("ssn", "email", "phone", "first_name", "last_name", "street").
public interface IApplicationPartyRepository
{
    Task<IReadOnlyList<string>> SearchAsync(string field, string value, CancellationToken ct = default);
}
