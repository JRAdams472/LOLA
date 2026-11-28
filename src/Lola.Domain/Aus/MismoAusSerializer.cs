using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Lola.Domain.Aus;

/// <summary>
/// Serializes the application document into a MISMO 3.4 AUS request skeleton.
/// Element ordering and container nesting follow the authentic schema-valid
/// DU request fixture — output validates against the published DU wrapper
/// XSD in tests. Missing inputs render as absent elements — never invented
/// values (M24 rule). Income is intentionally not yet emitted: in MISMO 3.4
/// it lives under BORROWER/CURRENT_INCOME_ITEMS with relationship xlinks,
/// which is phase-2 scope when full loan data flows through the seam.
/// </summary>
public static class MismoAusSerializer
{
    public static readonly XNamespace Mismo = "http://www.mismo.org/residential/2009/schemas";
    public const string ReferenceModelIdentifier = "3.4.032420160128";

    public static string Serialize(IReadOnlyDictionary<string, string?> doc)
    {
        var terms = new XElement(Mismo + "TERMS_OF_LOAN",
            new XElement(Mismo + "BaseLoanAmount", Get(doc, "loan.amount")),
            Get(doc, "loan.purpose") is { } purpose
                ? new XElement(Mismo + "LoanPurposeType", purpose) : null);

        // DEAL child order is schema-significant: LOANS before PARTIES.
        var deal = new XElement(Mismo + "DEAL",
            new XElement(Mismo + "LOANS",
                new XElement(Mismo + "LOAN", terms)),
            new XElement(Mismo + "PARTIES",
                new XElement(Mismo + "PARTY",
                    new XElement(Mismo + "INDIVIDUAL",
                        new XElement(Mismo + "NAME",
                            new XElement(Mismo + "FirstName", Get(doc, "borrower.first_name")),
                            new XElement(Mismo + "LastName", Get(doc, "borrower.last_name")))),
                    new XElement(Mismo + "ROLES",
                        new XElement(Mismo + "ROLE",
                            new XElement(Mismo + "BORROWER",
                                new XElement(Mismo + "BORROWER_DETAIL",
                                    new XElement(Mismo + "BorrowerBirthDate", Get(doc, "borrower.date_of_birth")))))))));

        var message = new XElement(Mismo + "MESSAGE",
            new XAttribute("MISMOReferenceModelIdentifier", ReferenceModelIdentifier),
            new XElement(Mismo + "ABOUT_VERSIONS",
                new XElement(Mismo + "ABOUT_VERSION",
                    new XElement(Mismo + "CreatedDatetime",
                        DateTime.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)))),
            new XElement(Mismo + "DEAL_SETS",
                new XElement(Mismo + "DEAL_SET",
                    new XElement(Mismo + "DEALS", deal))));

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), message);
        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
            document.Save(sw, SaveOptions.None);
        return sb.ToString();
    }

    internal static string? Get(IReadOnlyDictionary<string, string?> doc, string path)
        => doc.TryGetValue(path, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
