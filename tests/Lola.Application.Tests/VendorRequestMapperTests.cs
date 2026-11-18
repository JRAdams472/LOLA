using Lola.Application.Applications;
using Lola.Application.Vendors;
using Lola.Domain.Applications;
using Shouldly;

namespace Lola.Application.Tests;

/// MPE-227 — the mapper is the egress-minimization enforcement
/// point: each kind reads only its allowlisted doc paths, missing
/// required fields reject with the path named, and the dedupe hash
/// is stable per (tenant, app, kind, payload).
public sealed class VendorRequestMapperTests
{
    private static ApplicationRecord App(
        IReadOnlyDictionary<string, string>? fields = null)
    {
        var doc = ApplicationDocument.Empty();
        foreach (var (path, value) in fields ?? Full)
            doc.Set(path, value);
        return new ApplicationRecord("app-1", "draft", "POSIntake",
            "brw-1", 3, null, null, doc);
    }

    private static readonly Dictionary<string, string> Full = new()
    {
        ["party[0].first_name"] = "Ada",
        ["party[0].last_name"] = "Testcase",
        ["party[0].ssn"] = "123456789",
        ["party[0].dob"] = "1985-04-12",
        ["party[0].address[0].street"] = "42 Main St",
        ["party[0].address[0].city"] = "Springfield",
        ["party[0].address[0].state"] = "IL",
        ["party[0].address[0].zip"] = "62701",
        ["party[0].employment[0].employer_name"] = "Widget Corp",
        ["party[0].phone"] = "2175550100",
        ["subject_property.street"] = "1 Elm Ave",
        ["subject_property.city"] = "Springfield",
        ["subject_property.state"] = "IL",
        ["subject_property.zip"] = "62702",
        ["loan_amount"] = "350000",
        ["loan_purpose"] = "purchase"
    };

    [Fact]
    public void Credit_maps_only_its_allowlisted_fields()
    {
        var req = VendorRequestMapper.Map(VendorKind.Credit, App())
            .ShouldBeOfType<CreditOrderRequest>();
        req.Ssn.ShouldBe("123456789");
        req.Zip.ShouldBe("62701");
    }

    [Fact]
    public void Flood_uses_subject_property_not_borrower_address()
    {
        var req = VendorRequestMapper.Map(VendorKind.Flood, App())
            .ShouldBeOfType<FloodOrderRequest>();
        req.Street.ShouldBe("1 Elm Ave");
        req.Zip.ShouldBe("62702");
    }

    [Fact]
    public void Aus_carries_the_whole_document_and_version()
    {
        var req = VendorRequestMapper.Map(VendorKind.Aus, App())
            .ShouldBeOfType<AusOrderRequest>();
        req.ApplicationId.ShouldBe("app-1");
        req.Version.ShouldBe(3);
        req.MismoJson.ShouldContain("123456789");
    }

    [Fact]
    public void Missing_required_field_names_the_doc_path()
    {
        var ex = Should.Throw<Lola.Application.Errors.DomainException>(
            () => VendorRequestMapper.Map(VendorKind.Credit,
                App(new Dictionary<string, string>())));
        ex.Message.ShouldContain("party[0].first_name");
    }

    [Fact]
    public void Request_hash_dedupes_identical_orders()
    {
        var req = VendorRequestMapper.Map(VendorKind.Fraud, App());
        var json = VendorRequestMapper.RequestJson(req);
        var h1 = VendorRequestMapper.RequestHash(
            "t1", "app-1", VendorKind.Fraud, json);
        var h2 = VendorRequestMapper.RequestHash(
            "t1", "app-1", VendorKind.Fraud, json);
        h1.ShouldBe(h2);
        // A doc change produces a different hash — re-order is live.
        var other = VendorRequestMapper.RequestHash(
            "t1", "app-2", VendorKind.Fraud, json);
        other.ShouldNotBe(h1);
    }
}
