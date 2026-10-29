using System.Text.Json.Nodes;

namespace Lola.Application.Applications;

/// Structured field-level error — the operator-correction contract
/// (MPE-110): every validation failure names its catalog path, a
/// machine code, and a human message.
public sealed record FieldError(string Path, string Code, string Message);

/// Outbound application — `Doc` is the JSON1 doc with catalog PII
/// masked per the caller's role→visibility (DocMasker, invariant 8).
public sealed record ApplicationDto(
    string Id, string Status, string PipelineStep, string? BorrowerUserId,
    int CurrentVersion, string? TridClockStart, string? SubmittedUtc,
    JsonObject Doc);

/// Submit/correct result — `Ok=false` carries the FieldError list the
/// endpoint surfaces as a 422 problem extension.
public sealed record ApplicationWriteResult(
    bool Ok, int? Version, IReadOnlyList<FieldError> Errors)
{
    public static ApplicationWriteResult Success(int version) => new(true, version, []);
    public static ApplicationWriteResult Failed(IReadOnlyList<FieldError> errors) =>
        new(false, null, errors);
}
