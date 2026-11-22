using Lola.Application.Audit;
using Lola.Application.Errors;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-274 — the reason-code vocabulary is the ONLY thing a reveal or
/// retention-override writes to the chain; rejected input is never
/// echoed into the exception (no log/trace/audit path can catch it).
public sealed class ReasonCodeTests
{
    [Theory]
    [InlineData("reveal.borrower_request")]
    [InlineData("reveal.underwriting_review")]
    [InlineData("reveal.compliance_audit")]
    public void Reveal_codes_validate(string code)
        => ReasonCodes.Require(code, ReasonCodes.Reveal).ShouldBe(code);

    [Theory]
    [InlineData("erasure.borrower_request")]
    [InlineData("erasure.hold_release")]
    [InlineData("erasure.regulatory_order")]
    public void Erasure_codes_validate(string code)
        => ReasonCodes.Require(code, ReasonCodes.Erasure).ShouldBe(code);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("borrower SSN 123-45-6789")]
    [InlineData("reveal.borrower_request'; DROP TABLE audit_events--")]
    [InlineData("erasure.borrower_request")] // wrong category
    public void Reveal_rejects_everything_else_without_echo(string? bad)
    {
        var ex = Should.Throw<DomainException>(
            () => ReasonCodes.Require(bad, ReasonCodes.Reveal));
        ex.Message.ShouldBe("invalid reason code");
        if (!string.IsNullOrEmpty(bad)) ex.Message.ShouldNotContain(bad);
    }
}
