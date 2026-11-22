using Lola.Application.Errors;

namespace Lola.Application.Audit;

/// Validated audit reason codes (MPE-274) — operator free-text
/// reasons were an injection channel: a name, SSN, or narrative
/// typed into a reveal/erasure prompt landed verbatim in the
/// append-only audit chain and every log/trace that carried it.
/// Approved codes are fixed vocabulary; anything else is rejected
/// WITHOUT echoing the supplied value into the error, logs, or
/// telemetry. Narratives that are genuinely needed belong in
/// separately classified storage referenced by the event — never
/// the chain itself.
public static class ReasonCodes
{
    /// PII reveal (POST …/{appId}/reveal).
    public const string RevealBorrowerRequest = "reveal.borrower_request";
    public const string RevealUnderwritingReview =
        "reveal.underwriting_review";
    public const string RevealComplianceAudit = "reveal.compliance_audit";

    /// Retention-override erasure (DELETE /api/admin/tenants/{id}
    /// inside the Reg B window).
    public const string ErasureBorrowerRequest = "erasure.borrower_request";
    public const string ErasureHoldRelease = "erasure.hold_release";
    public const string ErasureRegulatoryOrder = "erasure.regulatory_order";

    public static readonly IReadOnlyList<string> Reveal =
        [RevealBorrowerRequest, RevealUnderwritingReview,
            RevealComplianceAudit];

    public static readonly IReadOnlyList<string> Erasure =
        [ErasureBorrowerRequest, ErasureHoldRelease,
            ErasureRegulatoryOrder];

    /// Code or BadRequest — the error names the category, never the
    /// rejected input, so a PII marker in `code` cannot reach the
    /// response body, a log line, or a trace tag.
    public static string Require(string? code,
        IReadOnlyList<string> allowed)
    {
        if (code is not null && allowed.Contains(code))
            return code;
        throw DomainException.BadRequest("invalid reason code");
    }
}
