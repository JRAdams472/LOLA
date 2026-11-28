using System.Xml.Linq;
using System.Xml.Schema;
using System.Xml;
using Lola.Domain.Aus;
using Xunit;

namespace Lola.Domain.Tests;

public class AusTests
{
    private static string FixtureRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Lola.slnx"))
            && !File.Exists(Path.Combine(dir.FullName, "Lola.slnx")))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "tests", "fixtures", "aus");
    }

    private static string ReadFixture(params string[] parts)
        => File.ReadAllText(Path.Combine(new[] { FixtureRoot() }.Concat(parts).ToArray()));

    // --- DU parser: synthetic CFF fixtures ---

    [Fact]
    public void Du_approve_eligible_fixture_parses()
    {
        var xml = ReadFixture("du", "du-response-approve-eligible-synthetic.xml");
        var result = DuResponseParser.Parse(xml);
        Assert.Equal("du", result.Vendor);
        Assert.Equal(AusRecommendation.ApproveEligible, result.Recommendation);
        Assert.Equal("Approve", result.RiskClass);
        Assert.Equal("DU-SYNTHETIC-CASE-0001", result.CaseId);
        Assert.Equal(2, result.Findings.Count);
        Assert.Contains(result.Findings, f => f.VendorCode == "3893");
    }

    [Fact]
    public void Du_refer_with_caution_fixture_parses_as_refer()
    {
        var xml = ReadFixture("du", "du-response-refer-with-caution-synthetic.xml");
        var result = DuResponseParser.Parse(xml);
        Assert.Equal(AusRecommendation.Refer, result.Recommendation);
        Assert.NotEmpty(result.Findings);
    }

    [Fact]
    public void Du_malformed_xml_returns_unable_to_evaluate_never_throws()
    {
        var result = DuResponseParser.Parse("<not xml");
        Assert.Equal(AusRecommendation.UnableToEvaluate, result.Recommendation);
        var f = Assert.Single(result.Findings);
        Assert.Equal(AusFindingSeverity.Fatal, f.Severity);
    }

    [Fact]
    public void Du_findings_carry_no_borrower_pii_shape()
    {
        var xml = ReadFixture("du", "du-response-approve-eligible-synthetic.xml");
        var result = DuResponseParser.Parse(xml);
        Assert.All(result.Findings, f =>
        {
            Assert.DoesNotContain("ssn", f.Message, StringComparison.OrdinalIgnoreCase);
            Assert.False(string.IsNullOrWhiteSpace(f.VendorCode));
        });
    }

    // --- LPA parser: synthetic feedback-certificate fixtures ---

    [Fact]
    public void Lpa_accept_fixture_parses()
    {
        var xml = ReadFixture("lpa", "lpa-response-accept-synthetic.xml");
        var result = LpaResponseParser.Parse(xml);
        Assert.Equal("lpa", result.Vendor);
        Assert.Equal(AusRecommendation.Accept, result.Recommendation);
        Assert.Equal("Accept", result.RiskClass);
        Assert.Equal("LPA-TXN-SYNTHETIC-0001", result.CaseId);
        Assert.NotEmpty(result.Findings);
        Assert.Contains(result.Findings, f => f.VendorCode == "SYNTHETIC-001");
    }

    [Fact]
    public void Lpa_caution_fixture_parses_as_caution()
    {
        var xml = ReadFixture("lpa", "lpa-response-caution-synthetic.xml");
        var result = LpaResponseParser.Parse(xml);
        Assert.Equal(AusRecommendation.Caution, result.Recommendation);
    }

    [Fact]
    public void Lpa_malformed_xml_returns_unable_to_evaluate_never_throws()
    {
        var result = LpaResponseParser.Parse("not xml at all");
        Assert.Equal(AusRecommendation.UnableToEvaluate, result.Recommendation);
        Assert.Single(result.Findings);
    }

    // --- Normalizer vocabulary coverage ---

    [Theory]
    [InlineData("REP-CREDIT-SCORE LIST", AusFindingCategory.Credit)]
    [InlineData("INCOME", AusFindingCategory.Income)]
    [InlineData("Assets", AusFindingCategory.Asset)]
    [InlineData("collateral", AusFindingCategory.Collateral)]
    [InlineData("DocumentationMessages", AusFindingCategory.Other)]
    [InlineData(null, AusFindingCategory.Other)]
    public void Normalizer_categories(string? input, AusFindingCategory expected)
        => Assert.Equal(expected, AusNormalizer.ClassifyCategory(input));

    [Theory]
    [InlineData("Observation", AusFindingSeverity.Info)]
    [InlineData("Fatal", AusFindingSeverity.Fatal)]
    [InlineData("Warning", AusFindingSeverity.Warning)]
    [InlineData(null, AusFindingSeverity.Info)]
    public void Normalizer_severities(string? input, AusFindingSeverity expected)
        => Assert.Equal(expected, AusNormalizer.ClassifySeverity(input));

    // --- Serializer ---

    [Fact]
    public void Serializer_emits_mismo_34_shape()
    {
        var doc = new Dictionary<string, string?>
        {
            ["borrower.first_name"] = "Ava",
            ["borrower.last_name"] = "Tester",
            ["borrower.date_of_birth"] = "1985-04-12",
            ["loan.amount"] = "300000",
            ["loan.purpose"] = "Purchase",
            ["income.employment.monthly_total"] = "9000",
        };
        var xml = MismoAusSerializer.Serialize(doc);
        var parsed = XDocument.Parse(xml);
        Assert.Equal("MESSAGE", parsed.Root!.Name.LocalName);
        Assert.Equal("http://www.mismo.org/residential/2009/schemas", parsed.Root.Name.NamespaceName);
        Assert.Equal("3.4.032420160128", parsed.Root.Attribute("MISMOReferenceModelIdentifier")!.Value);
        Assert.Equal("Ava", parsed.Descendants().First(e => e.Name.LocalName == "FirstName").Value);
        Assert.Equal("300000", parsed.Descendants().First(e => e.Name.LocalName == "BaseLoanAmount").Value);
    }

    [Fact]
    public void Serializer_omits_missing_inputs_never_invents()
    {
        var xml = MismoAusSerializer.Serialize(new Dictionary<string, string?>
        {
            ["borrower.first_name"] = "Ava",
        });
        var parsed = XDocument.Parse(xml);
        Assert.DoesNotContain(parsed.Descendants(), e => e.Name.LocalName == "BaseLoanAmount"
            && !string.IsNullOrWhiteSpace(e.Value));
    }

    [Fact]
    public void Serializer_output_validates_against_published_mismo_xsd()
    {
        var mismoDir = Path.Combine(FixtureRoot(), "mismo");
        var wrapper = Path.Combine(mismoDir, "DU_Wrapper_3.4.0_B324.xsd");
        Assert.True(File.Exists(wrapper), "published DU wrapper XSD must be vendored in fixtures");

        var settings = new XmlSchemaSet();
        var resolver = new XmlUrlResolver();
        settings.XmlResolver = resolver;
        using (var reader = System.Xml.XmlReader.Create(wrapper,
            new System.Xml.XmlReaderSettings { XmlResolver = resolver }))
            settings.Add("http://www.mismo.org/residential/2009/schemas", reader);
        settings.Compile();

        var doc = new Dictionary<string, string?>
        {
            ["borrower.first_name"] = "Ava",
            ["borrower.last_name"] = "Tester",
            ["borrower.date_of_birth"] = "1985-04-12",
            ["loan.amount"] = "300000",
            ["loan.purpose"] = "Purchase",
        };
        var xml = MismoAusSerializer.Serialize(doc);
        var parsed = XDocument.Parse(xml);

        var errors = new List<string>();
        parsed.Validate(settings, (_, e) => errors.Add(e.Message));
        Assert.True(errors.Count == 0,
            "serializer output must validate against the published DU wrapper XSD: "
            + string.Join(" | ", errors.Take(3)));
    }

    [Fact]
    public void Authentic_du_request_fixture_validates_against_wrapper_xsd()
    {
        var mismoDir = Path.Combine(FixtureRoot(), "mismo");
        var wrapper = Path.Combine(mismoDir, "DU_Wrapper_3.4.0_B324.xsd");
        var settings = new XmlSchemaSet { XmlResolver = new XmlUrlResolver() };
        using (var reader = System.Xml.XmlReader.Create(wrapper,
            new System.Xml.XmlReaderSettings { XmlResolver = new XmlUrlResolver() }))
            settings.Add("http://www.mismo.org/residential/2009/schemas", reader);
        settings.Compile();

        var xml = ReadFixture("du", "du-request-di-c01-public-copy.xml");
        var parsed = XDocument.Parse(xml);
        var errors = new List<string>();
        parsed.Validate(settings, (_, e) => errors.Add(e.Message));
        Assert.True(errors.Count == 0,
            "authentic DU request fixture must validate: " + string.Join(" | ", errors.Take(3)));
    }

    // --- Recommendation mapping completeness ---

    [Theory]
    [InlineData("ApproveEligible", AusRecommendation.ApproveEligible)]
    [InlineData("Approve/Eligible", AusRecommendation.ApproveEligible)]
    [InlineData("ReferWithCaution4", AusRecommendation.Refer)]
    [InlineData("Ineligible", AusRecommendation.Ineligible)]
    [InlineData("garbage", AusRecommendation.UnableToEvaluate)]
    [InlineData(null, AusRecommendation.UnableToEvaluate)]
    public void Du_recommendation_mapping(string? input, AusRecommendation expected)
        => Assert.Equal(expected, DuResponseParser.MapRecommendation(input));

    [Theory]
    [InlineData("Accept", AusRecommendation.Accept)]
    [InlineData("Caution", AusRecommendation.Caution)]
    [InlineData("Refer", AusRecommendation.Refer)]
    [InlineData(null, AusRecommendation.UnableToEvaluate)]
    public void Lpa_recommendation_mapping(string? input, AusRecommendation expected)
        => Assert.Equal(expected, LpaResponseParser.MapRecommendation(input));

    [Fact]
    public void DTD_bearing_vendor_xml_is_rejected_not_expanded()
    {
        // XXE hardening: a DTD payload — entity expansion or external
        // entity — never parses; both parsers degrade to the typed
        // UnableToEvaluate failure, never throw, never expand.
        const string dtdPayload = """
            <?xml version="1.0"?>
            <!DOCTYPE r [ <!ENTITY xxe SYSTEM "file:///c:/windows/win.ini"> ]>
            <Casefile><UnderwritingRecommendation RecommendationType="Approve"/></Casefile>
            """;
        var du = DuResponseParser.Parse(dtdPayload);
        Assert.Equal(AusRecommendation.UnableToEvaluate, du.Recommendation);
        Assert.Contains(du.Findings, f => f.Severity == AusFindingSeverity.Fatal);
        var lpa = LpaResponseParser.Parse(dtdPayload);
        Assert.Equal(AusRecommendation.UnableToEvaluate, lpa.Recommendation);
    }
}
