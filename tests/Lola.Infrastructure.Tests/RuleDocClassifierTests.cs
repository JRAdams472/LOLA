using System.Text;
using Lola.Application.Vault;
using Lola.Infrastructure.Vault;
using Shouldly;
using Xunit;

namespace Lola.Infrastructure.Tests;

/// M18 P1 — the deterministic rule classifier: kind detection,
/// extraction, and finding rules on synthetic doc heads.
public class RuleDocClassifierTests
{
    private sealed class FakeClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static readonly DateTimeOffset Now =
        new(2026, 3, 1, 0, 0, 0, TimeSpan.Zero);

    private static VaultDocRecord Doc(string declared = "w2",
        string filename = "w2.txt", string contentType = "text/plain")
        => new("vlt-test", "app-1", declared, filename, contentType,
            100, "sha", "dek", "stub", "quarantine/vlt-test",
            "quarantined", null, "actor", "now", null, "hash");

    private static RuleDocClassifier Classifier()
        => new(new FakeClock(Now));

    private const string W2Text = """
        FORM W-2 WAGE AND TAX STATEMENT
        Employer Name: Acme Lending Corp
        Employer Address: 100 Main St, Austin, TX 78701
        Employer Identification Number: 12-3456789
        Tax Year: 2025
        Wages, tips, other compensation: 85,400.00
        """;

    private const string PaystubText = """
        EARNINGS STATEMENT — PAY STUB
        Employer Name: Acme Lending Corp
        Employer Address: 100 Main St, Austin, TX 78701
        Pay Period End: {0}
        Gross Pay: $4,200.00
        Net Pay: $3,150.00
        """;

    private const string BankText = """
        FIRST SYNTHETIC BANK — ACCOUNT SUMMARY
        Statement Period: monthly
        Statement Date: {0}
        Beginning Balance: $1,200.00
        Ending Balance: $2,050.00
        Routing Number: 111000025
        """;

    [Fact]
    public async Task W2_classifies_with_extracted_fields()
    {
        var c = await Classifier().ClassifyAsync(Doc(),
            Encoding.ASCII.GetBytes(W2Text));
        c.DocKind.ShouldBe(DocKind.W2);
        c.Confidence.ShouldBeGreaterThan(0.7);
        c.Fields["employer_name"].ShouldBe("Acme Lending Corp");
        c.Fields["tax_year"].ShouldBe("2025");
        c.Fields["employer_address"].ShouldContain("Austin");
        c.Fields["wages"].ShouldBe("85,400.00");
        c.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Recent_paystub_classifies_fresh()
    {
        var c = await Classifier().ClassifyAsync(
            Doc("paystub", "paystub.txt"),
            Encoding.ASCII.GetBytes(
                string.Format(PaystubText, "02/15/2026")));
        c.DocKind.ShouldBe(DocKind.Paystub);
        c.Fields["period_end"].ShouldBe("02/15/2026");
        c.Fields["gross_pay"].ShouldBe("4,200.00");
        c.Findings.ShouldBeEmpty();
    }

    [Fact]
    public async Task Stale_paystub_flags_expired_doc()
    {
        var c = await Classifier().ClassifyAsync(
            Doc("paystub", "paystub.txt"),
            Encoding.ASCII.GetBytes(
                string.Format(PaystubText, "08/01/2025")));
        c.DocKind.ShouldBe(DocKind.Paystub);
        c.Findings.ShouldContain(
            f => f.Kind == DocFindingKind.ExpiredDoc);
    }

    [Fact]
    public async Task Old_bank_statement_flags_expired()
    {
        var c = await Classifier().ClassifyAsync(
            Doc("bank_statement", "statement.txt"),
            Encoding.ASCII.GetBytes(
                string.Format(BankText, "10/01/2025")));
        c.DocKind.ShouldBe(DocKind.BankStatement);
        c.Findings.ShouldContain(
            f => f.Kind == DocFindingKind.ExpiredDoc);
    }

    [Fact]
    public async Task Expired_id_flags_expired_doc()
    {
        var text = """
            DRIVER LICENSE — STATE OF TEXAS
            Date of Birth: 01/15/1990
            Expiration Date: 01/01/2025
            """ + new string('x', 80);
        var c = await Classifier().ClassifyAsync(
            Doc("id", "driver-license.png", "image/png"),
            Encoding.ASCII.GetBytes(text));
        c.DocKind.ShouldBe(DocKind.Id);
        c.Findings.ShouldContain(
            f => f.Kind == DocFindingKind.ExpiredDoc);
    }

    [Fact]
    public async Task Current_id_does_not_flag_expiry()
    {
        var text = """
            DRIVER LICENSE — STATE OF TEXAS
            Date of Birth: 01/15/1990
            Expiration Date: 01/01/2031
            """ + new string('x', 80);
        var c = await Classifier().ClassifyAsync(
            Doc("id", "driver-license.png", "image/png"),
            Encoding.ASCII.GetBytes(text));
        c.Findings.ShouldNotContain(
            f => f.Kind == DocFindingKind.ExpiredDoc);
    }

    [Fact]
    public async Task Declared_vs_detected_mismatch_flags()
    {
        // Declared w2 but the head is clearly a bank statement.
        var c = await Classifier().ClassifyAsync(
            Doc("w2", "statement.txt"),
            Encoding.ASCII.GetBytes(
                string.Format(BankText, "02/01/2026")));
        c.DocKind.ShouldBe(DocKind.BankStatement);
        c.Findings.ShouldContain(
            f => f.Kind == DocFindingKind.KindMismatch);
    }

    [Fact]
    public async Task Binary_head_falls_back_and_flags_unreadable()
    {
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47 }
            .Concat(Enumerable.Range(0, 2000)
                .Select(i => (byte)(i % 13))).ToArray();
        var c = await Classifier().ClassifyAsync(
            Doc("w2", "blob.bin", "application/octet-stream"), bytes);
        c.DocKind.ShouldBe(DocKind.Misc);
        c.Findings.ShouldContain(
            f => f.Kind == DocFindingKind.Unreadable);
    }

    [Fact]
    public async Task Missing_required_extractor_flags_missing_field()
    {
        // W2 markers but no Employer Name line.
        var text = """
            FORM W-2 WAGE AND TAX STATEMENT
            Tax Year: 2025
            Wages, tips, other compensation: 10,000.00
            """ + new string('x', 80);
        var c = await Classifier().ClassifyAsync(Doc(),
            Encoding.ASCII.GetBytes(text));
        c.Findings.ShouldContain(f =>
            f.Kind == DocFindingKind.MissingField
            && f.Detail == "employer_name");
    }

    [Fact]
    public async Task Flood_cert_classifies_with_zone()
    {
        var text = """
            STANDARD FLOOD CERTIFICATION
            Flood Zone: AE
            Issue Date: 01/10/2026
            NFIP determination attached
            """ + new string('x', 80);
        var c = await Classifier().ClassifyAsync(
            Doc("flood_cert", "flood-cert.txt"),
            Encoding.ASCII.GetBytes(text));
        c.DocKind.ShouldBe(DocKind.FloodCert);
        c.Fields["flood_zone"].ShouldBe("AE");
    }

    [Fact]
    public async Task Deterministic_same_input_same_output()
    {
        var head = Encoding.ASCII.GetBytes(W2Text);
        var a = await Classifier().ClassifyAsync(Doc(), head);
        var b = await Classifier().ClassifyAsync(Doc(), head);
        (a.DocKind, a.Confidence).ShouldBe((b.DocKind, b.Confidence));
        a.Fields.ShouldBe(b.Fields);
        a.Findings.Select(f => (f.Kind, f.Detail))
            .ShouldBe(b.Findings.Select(f => (f.Kind, f.Detail)));
    }
}

/// M18 P1 — DOC_CLASSIFIER env swap: `stub` echoes the declared kind,
/// unknown names fail fast.
public class DocClassifierCatalogTests
{
    [Fact]
    public void Default_is_rule()
        => DocClassifierCatalog.Select(null, TimeProvider.System)
            .Adapter.ShouldBe("rule");

    [Fact]
    public void Stub_selects_stub()
        => DocClassifierCatalog.Select("stub", TimeProvider.System)
            .Adapter.ShouldBe("stub");

    [Fact]
    public void Unknown_fails_fast()
        => Should.Throw<InvalidOperationException>(
            () => DocClassifierCatalog.Select(
                "zen", TimeProvider.System));

    [Fact]
    public async Task Stub_echoes_declared_kind()
    {
        var c = await new StubDocClassifier().ClassifyAsync(
            new VaultDocRecord("vlt-1", "app-1", "w2", "w2.txt",
                "text/plain", 1, "s", "d", "stub", "q/vlt-1",
                "quarantined", null, "a", "t", null, "h"),
            ReadOnlyMemory<byte>.Empty);
        c.DocKind.ShouldBe("w2");
        c.Fields.ShouldBeEmpty();
        c.Findings.ShouldBeEmpty();
    }
}
