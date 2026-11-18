using System.Text.Json.Nodes;
using Lola.Domain.Classification;

namespace Lola.Application.Vendors;

/// Verification-service adapter seam (ADR-0007, M13/MPE-77). Core LOS
/// logic talks ONLY to this interface — the stub is the first
/// implementation; real bureaus/verification vendors arrive as
/// DTS-style transforms behind the same contract with zero core
/// changes. One adapter may serve several kinds (`Kinds` declares
/// coverage); `IVendorAdapterResolver` picks the adapter for an order.
///
/// Unlike pricing/fees, this egress legitimately carries PII — a
/// credit pull needs SSN, OFAC needs DOB. Invariant 2 holds as
/// minimum-fields-per-kind: every `VendorOrderRequest` member is
/// declared on `VendorEgress.Allowed[kind]` and the reflection
/// firewall proves the member set equals the allowlist exactly.
public interface IVendorAdapter
{
    /// Adapter identity stamped onto order rows ("stub", ...).
    string Adapter { get; }

    /// Service kinds this adapter fulfills.
    IReadOnlySet<VendorKind> Kinds { get; }

    /// Place the order. Returns the vendor's receipt — the external
    /// id the completion channels correlate on. `creds` is null when
    /// the tenant has no binding yet; adapters turn that into a typed
    /// auth failure, never a shared-credential fallback.
    Task<VendorReceipt> SubmitAsync(VendorOrderRequest order,
        VendorCredentials? creds, CancellationToken ct = default);

    /// Poll completion for vendors without webhook push. Null = the
    /// order is still in flight; the drain reschedules.
    Task<VendorResult?> PollAsync(VendorReceipt receipt,
        VendorCredentials? creds, CancellationToken ct = default);
}

/// Service kinds — the five seams the ticket names.
public enum VendorKind
{
    Credit,
    Voe,
    Voi,
    Flood,
    Fraud,
    Aus
}

/// Vendor-held credential material resolved from OpenBao KV at call
/// time (`lola-kv/vendors/{tenant}/{kind}`) — never config/DB/repo.
/// All members optional: a real vendor may need only an API key,
/// only a webhook secret, or both.
public sealed record VendorCredentials(
    string? ApiKey, string? WebhookSecret);

/// Vendor acknowledgement — the correlation handle for poll/webhook.
public sealed record VendorReceipt(string ExternalId, string ReceivedUtc);

/// Normalized inbound result. `Report` is the opaque per-kind JSON
/// document — the egress boundary's typed surface stays metadata-only
/// (the same JsonObject boundary `DocPorts` uses); per-kind report
/// shape is normalized by fixture contract in the stub tests.
public sealed record VendorResult(
    VendorKind Kind,
    string Outcome,          // completed|failed|referred
    string? FailureReason,
    JsonObject Report);

/// Order payload base — concrete per-kind records below. Each member
/// of a concrete request is either declared on the kind's egress
/// allowlist or the reflection firewall fails CI.
public abstract record VendorOrderRequest
{
    public abstract VendorKind Kind { get; }
}

/// Credit report (tri-merge) — the identity set a bureau needs to
/// pull a file.
public sealed record CreditOrderRequest(
    [property: PiiClass(PiiClass.Name)] string FirstName,
    [property: PiiClass(PiiClass.Name)] string LastName,
    [property: PiiClass(PiiClass.Ssn)] string Ssn,
    [property: PiiClass(PiiClass.Dob)] string Dob,
    [property: PiiClass(PiiClass.Address)] string Street,
    [property: PiiClass(PiiClass.Address)] string City,
    [property: PiiClass(PiiClass.Address)] string State,
    [property: PiiClass(PiiClass.Address)] string Zip)
    : VendorOrderRequest
{
    public override VendorKind Kind => VendorKind.Credit;
}

/// Verification of employment — identity + employer to verify.
public sealed record VoeOrderRequest(
    [property: PiiClass(PiiClass.Name)] string FirstName,
    [property: PiiClass(PiiClass.Name)] string LastName,
    [property: PiiClass(PiiClass.Ssn)] string Ssn,
    [property: PiiClass(PiiClass.Other)] string EmployerName,
    [property: PiiClass(PiiClass.Other)] string EmployerPhone)
    : VendorOrderRequest
{
    public override VendorKind Kind => VendorKind.Voe;
}

/// Verification of income — identity + employer whose payroll data
/// the vendor queries.
public sealed record VoiOrderRequest(
    [property: PiiClass(PiiClass.Name)] string FirstName,
    [property: PiiClass(PiiClass.Name)] string LastName,
    [property: PiiClass(PiiClass.Ssn)] string Ssn,
    [property: PiiClass(PiiClass.Other)] string EmployerName)
    : VendorOrderRequest
{
    public override VendorKind Kind => VendorKind.Voi;
}

/// Flood certification — subject-property location only; no borrower
/// identity leaves the system for this kind.
public sealed record FloodOrderRequest(
    [property: PiiClass(PiiClass.Address)] string Street,
    [property: PiiClass(PiiClass.Address)] string City,
    [property: PiiClass(PiiClass.Address)] string State,
    [property: PiiClass(PiiClass.Address)] string Zip)
    : VendorOrderRequest
{
    public override VendorKind Kind => VendorKind.Flood;
}

/// Fraud/OFAC screening — identity minimum for list matching.
public sealed record FraudOrderRequest(
    [property: PiiClass(PiiClass.Name)] string FirstName,
    [property: PiiClass(PiiClass.Name)] string LastName,
    [property: PiiClass(PiiClass.Ssn)] string Ssn,
    [property: PiiClass(PiiClass.Dob)] string Dob)
    : VendorOrderRequest
{
    public override VendorKind Kind => VendorKind.Fraud;
}

/// AUS (DU/LP) — the serialized MISMO application plus its version;
/// underwriting needs the whole file. `MismoJson` is PII-dense by
/// construction — declared `Other` so the firewall and the masking
/// pipeline both see the boundary.
public sealed record AusOrderRequest(
    string ApplicationId,
    int Version,
    [property: PiiClass(PiiClass.Other)] string MismoJson)
    : VendorOrderRequest
{
    public override VendorKind Kind => VendorKind.Aus;
}
