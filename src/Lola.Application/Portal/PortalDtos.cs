using System.Text.Json.Nodes;

namespace Lola.Application.Portal;

/// Borrower-facing projections (M12, ADR-0008): the wire shape a
/// portal caller may EVER see — a deliberate, narrow subset. Anything
/// the officer console carries but the borrower must not
/// (pipeline_step, versions, actor ids, TRID internals, lock data,
/// vendor errors, hashes) simply does not exist on these records —
/// the contract test asserts the serialized key set.

/// One step of the borrower status tracker.
public sealed record PortalStageDto(
    string Key, string Label, bool Done, bool Current);

/// Registry package row, borrower view — `ready` is the only download
/// signal; vendor errors and adapter details never surface.
public sealed record PortalDocItem(
    string DocId, string PackageName, string KindLabel,
    bool Ready, string? Filename, string? CompletedUtc);

/// Vault upload row, borrower view — the uploader sees the friendly
/// scan outcome, never the raw quarantine status or error text.
public sealed record PortalUploadItem(
    string VaultDocId, string Classification, string StatusLabel,
    string StatusKey, string Filename, long SizeBytes, string? UploadedUtc);

/// The borrower's loan — status vocabulary from BorrowerStatusMap,
/// `Doc` masked by DocMasker under the borrower visibility context.
public sealed record PortalLoanDto(
    string Id, string StatusLabel, int StatusStage,
    IReadOnlyList<PortalStageDto> StatusStages,
    JsonObject Doc,
    IReadOnlyList<PortalDocItem> Documents,
    IReadOnlyList<PortalUploadItem> Uploads);

/// List-row shape for the dashboard — no payload doc.
public sealed record PortalLoanMeta(
    string Id, string StatusLabel, int StatusStage);
