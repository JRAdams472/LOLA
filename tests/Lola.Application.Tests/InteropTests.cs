using System.Xml.Linq;
using Lola.Application.Interop;
using Lola.Domain.Applications;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-111 — catalog-driven MISMO round-trip + ULDD structural export.
public sealed class InteropTests
{
    private static readonly FieldCatalog Catalog = FieldCatalog.Current;

    private static ApplicationDocument CompleteDoc()
    {
        var doc = ApplicationDocument.Empty();
        var patch = new Dictionary<string, string>
        {
            ["loan_amount"] = "425000",
            ["loan_purpose"] = "purchase",
            ["amortization_type"] = "fixed",
            ["loan_term_months"] = "360",
            ["party[0].role"] = "borrower",
            ["party[0].first_name"] = "Ada",
            ["party[0].last_name"] = "Lovelace",
            ["party[0].ssn"] = "123456789",
            ["party[0].dob"] = "1985-12-10",
            ["party[0].email"] = "ada@example.com",
            ["party[0].phone"] = "+1-555-0100",
            ["party[0].address[0].street"] = "12 Analytical Way",
            ["party[0].address[0].city"] = "Springfield",
            ["party[0].address[0].state"] = "IL",
            ["party[0].address[0].zip"] = "62701",
            ["party[0].employment[0].employer_name"] = "Acme Engines",
            ["party[0].employment[0].start_date"] = "2020-01-15",
            ["party[0].employment[0].self_employed"] = "false",
            ["party[0].employment[0].monthly_income"] = "14200",
            ["asset[0].asset_type"] = "checking",
            ["asset[0].institution"] = "First Bank",
            ["asset[0].account_number"] = "8844-2211",
            ["asset[0].balance"] = "85000",
            ["liability[0].liability_type"] = "auto",
            ["liability[0].creditor"] = "Car Loans Inc",
            ["liability[0].monthly_payment"] = "410",
            ["liability[0].balance"] = "12000",
            ["declaration.intent_to_occupy"] = "yes",
            ["declaration.first_time_buyer"] = "false",
            ["declaration.property_will_be"] = "primary_residence",
            ["subject_property.street"] = "1 Prime Plaza",
            ["subject_property.city"] = "Springfield",
            ["subject_property.state"] = "IL",
            ["subject_property.zip"] = "62701",
            ["subject_property.est_value"] = "560000",
            ["subject_property.property_type"] = "single_family",
            ["subject_property.usage"] = "primary_residence",
            ["demographics.ethnicity"] = "not_provided",
            ["demographics.race"] = "not_provided",
            ["demographics.sex"] = "not_provided",
            ["loan.base_amount"] = "425000",
            ["loan.ltv"] = "75.9",
            ["loan.lien_position"] = "first",
            ["loan.product_type"] = "conventional",
            // second borrower — index splicing
            ["party[1].role"] = "co_borrower",
            ["party[1].first_name"] = "Charles",
            ["party[1].last_name"] = "Babbage",
            ["party[1].ssn"] = "987654321",
        };
        foreach (var (p, v) in patch) doc.Set(p, v);
        return doc;
    }

    [Fact]
    public void Mismo_export_emits_catalog_paths_under_loan_application()
    {
        var xml = MismoXmlExporter.Export(CompleteDoc());
        var doc = XDocument.Parse(xml);
        doc.Root!.Name.LocalName.ShouldBe("MESSAGE");
        var parties = doc.Descendants()
            .Where(e => e.Name.LocalName == "PARTY").ToList();
        parties.Count.ShouldBe(2);
        parties[1].Descendants()
            .First(e => e.Name.LocalName == "FirstName").Value.ShouldBe("Charles");
        doc.Descendants().First(e => e.Name.LocalName == "LoanAmount")
            .Value.ShouldBe("425000");
    }

    [Fact]
    public void Mismo_roundtrip_preserves_every_catalog_value()
    {
        var original = CompleteDoc();
        var xml = MismoXmlExporter.Export(original);
        var (reimported, importedFields) = MismoXmlImporter.Import(xml);

        importedFields.ShouldBeGreaterThan(40);
        foreach (var (path, node) in original.Leaves())
        {
            if (Catalog.SpecFor(path)?.MismoPath is null) continue;
            var got = reimported.Get(path);
            got.ShouldNotBeNull($"path '{path}' was lost in the round-trip");
            var want = node is System.Text.Json.Nodes.JsonValue v &&
                v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
            (got is System.Text.Json.Nodes.JsonValue g &&
                g.TryGetValue<string>(out var t) ? t : got.ToJsonString())
                .ShouldBe(want, $"path '{path}'");
        }
    }

    [Fact]
    public void Importer_tolerates_unknown_elements_and_namespaces()
    {
        var xml = """
            <MESSAGE xmlns="http://example.com/foreign">
              <DEAL><LOAN_APPLICATION>
                <UNKNOWN_FUTURE_ELEMENT attr="x">ignored</UNKNOWN_FUTURE_ELEMENT>
                <LOANS><LOAN><LoanDetail>
                  <LoanAmount>300000</LoanAmount>
                  <VENDOR_EXTENSION>still ignored</VENDOR_EXTENSION>
                </LoanDetail></LOAN></LOANS>
              </LOAN_APPLICATION></DEAL>
            </MESSAGE>
            """;
        var (doc, imported) = MismoXmlImporter.Import(xml);
        doc.Get("loan_amount")!.ToString().ShouldBe("300000");
        doc.Get("vendor_extension").ShouldBeNull();
        imported.ShouldBe(1);
    }

    [Fact]
    public void Importer_finds_loan_application_in_mismo_envelope()
    {
        // The exporter nests LOAN_APPLICATION under
        // MESSAGE/DEAL_SETS/DEAL_SET/DEALS/DEAL — importing must locate
        // it anywhere, not only at root.
        var xml = MismoXmlExporter.Export(CompleteDoc());
        var (doc, _) = MismoXmlImporter.Import(xml);
        doc.Get("party[1].last_name")!.ToString().ShouldBe("Babbage");
    }

    [Fact]
    public void Importer_rejects_dtd_and_oversize_documents()
    {
        var dtd = """
            <?xml version="1.0"?>
            <!DOCTYPE r [ <!ENTITY x "boom"> ]>
            <MESSAGE><DEAL><LOAN_APPLICATION>&x;</LOAN_APPLICATION></DEAL></MESSAGE>
            """;
        Should.Throw<Exception>(() => MismoXmlImporter.Import(dtd));
        var huge = new string('x', 2_000_001);
        Should.Throw<ArgumentException>(() => MismoXmlImporter.Import(huge));
    }

    [Fact]
    public void Uldd_export_is_wellformed_with_required_containers()
    {
        var xml = UlddExporter.Export(CompleteDoc());
        var doc = XDocument.Parse(xml);
        doc.Root!.Name.LocalName.ShouldBe("ULDD");

        foreach (var container in new[]
            { "LOAN", "PROPERTY", "DECLARATION", "HMDA", "ASSET", "LIABILITY",
              "BORROWER" })
            doc.Root.Descendants().Any(e => e.Name.LocalName == container)
                .ShouldBeTrue($"required container '{container}' missing");

        doc.Descendants().First(e => e.Name.LocalName == "NoteAmount")
            .Value.ShouldBe("425000");
        // two borrowers — co-borrower is a sibling container, not merged
        doc.Root.Elements("BORROWER").Count().ShouldBe(2);
        // party-scoped containers nest inside their borrower
        var first = doc.Root.Element("BORROWER")!;
        first.Element("ADDRESS")!.Element("Street")!.Value
            .ShouldBe("12 Analytical Way");
        first.Element("EMPLOYER")!.Element("Name")!.Value
            .ShouldBe("Acme Engines");
        first.Element("EMPLOYER")!.Element("INCOME")!
            .Element("MonthlyAmount")!.Value.ShouldBe("14200");
    }
}
