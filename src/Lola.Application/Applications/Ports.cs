using Lola.Domain.Applications;

namespace Lola.Application.Applications;

/// One application row plus its current plaintext document.
/// `SubmittedUtc` non-null marks an immutable snapshot (invariant 6);
/// `TridClockStart` is stamped once (MPE-112).
public sealed record ApplicationRecord(
    string Id, string Status, string PipelineStep, string? BorrowerUserId,
    int CurrentVersion, string? TridClockStart, string? SubmittedUtc,
    ApplicationDocument Doc);

/// Pipeline row (M11): `BorrowerName` is party[0]'s composed name —
/// the repository decrypts inside the cipher boundary and the handler
/// masks it per VisibilityContext (officers see plaintext, everyone
/// else "•••"+last-4). `TridDeadlineUtc` is the Reg Z 3-business-day
/// deadline derived from the once-only clock stamp.
public sealed record ApplicationMeta(
    string Id, string Status, string PipelineStep,
    string? BorrowerUserId, long CurrentVersion, string? TridClockStart,
    string? BorrowerName, string? TridDeadlineUtc,
    string? UpdatedUtc = null)
{
    /// Opaque label for scoped reads where the caller must never see
    /// the decrypted name (MPE-266) — same "•••" family DocMasker
    /// emits, produced without paying a decrypt for a value that was
    /// only ever going to be masked.
    public const string MaskedName = "•••";
}

/// Bounded, scope-filtered pipeline page (MPE-266).
public sealed record ApplicationPage(
    IReadOnlyList<ApplicationMeta> Items, int Total, int Skip, int Take);

/// Minimal metadata + scope verdict for external-actor detail reads —
/// no payload load, no decrypt. `InScope=false` maps to Forbidden;
/// a null row maps to NotFound, preserving the M19 contract.
public sealed record ScopedApplicationMeta(
    string Id, string Status, string PipelineStep,
    string? BorrowerUserId, long CurrentVersion, string? TridClockStart,
    string? SubmittedUtc, bool InScope);

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

    /// Draft merge-patch (MPE-216): read→merge→append runs inside one
    /// write transaction so concurrent PATCHes serialize on the tenant
    /// file and compose — the old read-modify-write path let the last
    /// writer silently drop the other's disjoint fields. Returns the
    /// new version and the merged doc (plaintext — callers are inside
    /// CipherContext) for handlers that derive side-effects from it.
    Task<(int Version, ApplicationDocument Doc)> PatchDraftAsync(
        string id, IReadOnlyDictionary<string, string> patch,
        string writtenBy, CancellationToken ct = default);

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

    /// M19 partner row-scope: is `partnerUserId` actively linked to
    /// `id` via `application_partners` (revoked links don't count)?
    /// `RequireAppAsync` consults this for Partner actors.
    Task<bool> IsPartnerLinkedAsync(
        string id, string partnerUserId, CancellationToken ct = default);

    /// M19 partner list scope: app ids currently linked to
    /// `partnerUserId` — the Partner branch of the applications list.
    Task<IReadOnlyList<string>> LinkedAppIdsForPartnerAsync(
        string partnerUserId, CancellationToken ct = default);

    /// Scoped pipeline page (MPE-266): the borrower's-owner /
    /// active-partner-link predicate runs in SQL — before paging and
    /// before any name ciphertext loads — with deterministic order
    /// (created_utc DESC, id tie-break). Staff keeps the whole tenant.
    /// `includeNames=false` skips the party-name decrypt entirely and
    /// stamps `ApplicationMeta.MaskedName` where a name exists —
    /// external actors never hold Name visibility, so decrypting
    /// solely to mask is wasted ciphertext handling.
    Task<ApplicationPage> ListScopedAsync(ActorScope scope,
        string? actorId, int skip, int take, bool includeNames,
        CancellationToken ct = default);

    /// Minimal metadata + scope verdict for external-actor detail
    /// reads — no payload load, no decrypt, one scoped query (avoids a
    /// link-check→read gap). `InScope=false` maps to Forbidden; a null
    /// row maps to NotFound — the M19 contract is preserved.
    Task<ScopedApplicationMeta?> GetScopedMetaAsync(string id,
        ActorScope scope, string? actorId, CancellationToken ct = default);

    /// Single value off the CURRENT version payload via json_extract —
    /// callers name catalog non-PII paths only (the partner detail
    /// DTO's loan_amount). The payload column itself never leaves.
    Task<string?> GetDocValueAsync(string id, string path,
        CancellationToken ct = default);
}

/// One application_partners link row (M19). `RevokedUtc` non-null marks
/// a revoked link — the row stays for provenance; the scope guard and
/// link admin treat revoked as absent.
public sealed record PartnerLink(
    string AppId, string PartnerUserId, string PartnerRole,
    string LinkedBy, string LinkedUtc, string? RevokedUtc);

/// Outcome of an atomic access change (MPE-274): the state write,
/// tenant audit row, and loan event row commit together or roll
/// back together inside one tenant-file transaction.
public enum PartnerLinkOutcome
{
    /// Active link written (first link or re-link of a revoked row)
    /// with its audit + event evidence.
    Linked,
    /// An identical active link already existed — state unchanged,
    /// NO duplicate evidence written.
    NoOp,
    /// Revoke found no active link — nothing written (404 contract).
    NotFound,
    /// Active link revoked with its audit + event evidence.
    Revoked,
}

/// Staff-side administration of application_partners (M19): link,
/// revoke, and enumerate an app's partners. The reads a Partner
/// actor's own scope needs live on IApplicationRepository — this port
/// is the write/admin side only.
public interface IApplicationPartnerRepository
{
    /// Re-link after revoke clears `revoked_utc` and refreshes
    /// linked_by/linked_utc — the row is current-state, events/audit
    /// carry the history.
    Task LinkAsync(PartnerLink link, CancellationToken ct = default);

    /// Returns false when no ACTIVE link exists — unlink is honest
    /// 404, not silent no-op.
    Task<bool> RevokeAsync(string appId, string partnerUserId,
        string revokedUtc, CancellationToken ct = default);

    /// Every link row for an app, newest first — including revoked.
    Task<IReadOnlyList<PartnerLink>> ListForAppAsync(
        string appId, CancellationToken ct = default);

    /// Atomic access change (MPE-274): link state, tenant audit row
    /// and loan event row commit inside ONE tenant-file transaction —
    /// hash predecessors are read in-tx, and any failure rolls back
    /// all three. `NoOp` means an identical active link already
    /// existed: state unchanged and NO duplicate evidence written.
    Task<PartnerLinkOutcome> CommitLinkAsync(PartnerLink link,
        Audit.AuditDraft audit, Events.LoanEventDraft loanEvent,
        CancellationToken ct = default);

    /// Same atomicity for revoke. `NotFound` means no active link —
    /// nothing written (the handler keeps the 404 contract).
    Task<PartnerLinkOutcome> CommitRevokeAsync(string appId,
        string partnerUserId, string revokedUtc,
        Audit.AuditDraft audit, Events.LoanEventDraft loanEvent,
        CancellationToken ct = default);
}

/// Blind-index equality search — `field` is a catalog trailing key
/// ("ssn", "email", "phone", "first_name", "last_name", "street").
public interface IApplicationPartyRepository
{
    Task<IReadOnlyList<string>> SearchAsync(string field, string value, CancellationToken ct = default);
}
