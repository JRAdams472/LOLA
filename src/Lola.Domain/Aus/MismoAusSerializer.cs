using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace Lola.Domain.Aus;

/// <summary>
/// Serializes the application document into a MISMO 3.4 AUS request
/// (DU/LPA common submission shape). Missing inputs render as absent
/// elements — never invented values (M24 rule). Fixture-verified against
/// the published MISMO reference XSD in tests.
/// </summary>
public static class MismoAusSerializer
{
    public static readonly XNamespace Mismo = "http://www.mismo.org/residential/2009/schemas";

    public static string Serialize(IReadOnlyDictionary<string, string?> doc)
    {
        var loan = new XElement(Mismo + "LOANS",
            new XElement(Mismo + "LOAN",
                new XElement(Mismo + "LOAN_DETAIL",
                    new XElement(Mismo + "LoanPurposeType", Get(doc, "loan.purpose"))),
                new XElement(Mismo + "TERMS_OF_LOAN",
                    new XElement(Mismo + "LoanAmount", Get(doc, "loan.amount")),
                    Get(doc, "income.employment.monthly_total") is { } inc
                        ? new XElement(Mismo + "TotalMonthlyIncomeAmount", inc) : null)));

        var parties = new XElement(Mismo + "PARTIES",
            new XElement(Mismo + "PARTY",
                new XElement(Mismo + "INDIVIDUAL",
                    new XElement(Mismo + "NAME",
                        new XElement(Mismo + "FirstName", Get(doc, "borrower.first_name")),
                        new XElement(Mismo + "LastName", Get(doc, "borrower.last_name")))),
                new XElement(Mismo + "ROLES",
                    new XElement(Mismo + "ROLE",
                        new XElement(Mismo + "ROLE_DETAIL",
                            new XElement(Mismo + "PartyRoleType", "Borrower")),
                        new XElement(Mismo + "BORROWER",
                            new XElement(Mismo + "BORROWER_DETAIL",
                                new XElement(Mismo + "BorrowerBirthDate", Get(doc, "borrower.date_of_birth"))))))));

        var message = new XElement(Mismo + "MESSAGE",
            new XElement(Mismo + "ABOUT_VERSIONS",
                new XElement(Mismo + "ABOUT_VERSION",
                    new XElement(Mismo + "MismoVersionIdentifier", "3.4.0"))),
            new XElement(Mismo + "DEAL_SETS",
                new XElement(Mismo + "DEAL_SET",
                    new XElement(Mismo + "DEALS",
                        new XElement(Mismo + "DEAL", parties, loan)))));

        var document = new XDocument(new XDeclaration("1.0", "utf-8", null), message);
        var sb = new StringBuilder();
        using (var sw = new StringWriter(sb, CultureInfo.InvariantCulture))
            document.Save(sw, SaveOptions.None);
        return sb.ToString();
    }

    internal static string? Get(IReadOnlyDictionary<string, string?> doc, string path)
        => doc.TryGetValue(path, out var v) && !string.IsNullOrWhiteSpace(v) ? v : null;
}
