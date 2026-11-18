using System.Text.Json.Nodes;

namespace Lola.Application.Vendors;

/// Staff read projection — PII-free metadata always; `Result` is the
/// decrypted report envelope and only populated on the audited
/// detail read. Borrowers never reach this DTO (handler guards).
public sealed record VendorOrderDto(
    string OrderId, string ApplicationId, string Kind,
    string Adapter, string? ExternalId, string Status,
    string? Error, string OrderedBy, string? CreatedUtc,
    string? SubmittedUtc, string? CompletedUtc,
    JsonObject? Result);
