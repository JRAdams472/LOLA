using System.Text.Json.Nodes;
using Lola.Application.Errors;
using Lola.Application.Fees;
using Lola.Application.Pricing;
using Lola.Domain.Applications;

namespace Lola.Application.Documents;

/// Master payload assembly (MPE-72): 1003 doc + locked pricing +
/// aggregated fees → per-template payload objects bounded by
/// `Templates/manifest.json`. This is the ONE place PII-class fields
/// (borrower name, addresses) are marshalled for egress — every key
/// written must appear in the template's manifest allowlist; the
/// firewall test proves emitted == manifest, no drift either way.
public static class DisclosurePayloadBuilder
{
    /// Reg B adverse-action reason codes — enumerated so no free-text
    /// reason can carry PII (see MPE-187's free-text concern).
    public static readonly IReadOnlyDictionary<string, string>
        ReasonTexts = new Dictionary<string, string>
        {
            ["credit-history"] = "Credit history",
            ["insufficient-income"] =
                "Income insufficient for amount requested",
            ["employment"] = "Employment history",
            ["collateral"] = "Value or type of collateral",
            ["incomplete-application"] = "Incomplete application",
            ["excessive-obligations"] = "Excessive obligations",
        };

    public const string LePackage = "le-package";
    public const string AdverseAction = "adverse-action";

    /// Build the package's documents for `kind`. `lock_`+`fees` are
    /// required for le-package (the LE prices from them); adverse-action
    /// needs neither but requires `reasonCodes`.
    public static IReadOnlyList<DocPackageDocument> Build(
        string kind, string applicationId, ApplicationDocument doc,
        RateLockRecord? lock_, FeeBreakdownDto? fees,
        IReadOnlyList<string>? reasonCodes = null)
    {
        var templates = TemplateManifest.TemplatesForKind(kind);
        return templates.Select(t => kind switch
        {
            AdverseAction => For(t,
                AdversePayload(applicationId, doc, reasonCodes)),
            LePackage => For(t, LePayload(t, doc, lock_, fees)),
            _ => throw DomainException.BadRequest(
                $"unknown package kind '{kind}'")
        }).ToList();
    }

    private static DocPackageDocument For(string templateId,
        JsonObject payload)
    {
        // The minimization gate — every emitted key must be declared.
        var allow = TemplateManifest.FieldsFor(templateId);
        var extra = payload.Select(kv => kv.Key)
            .Where(k => !allow.Contains(k)).ToList();
        if (extra.Count > 0)
            throw new InvalidOperationException(
                $"payload for '{templateId}' emits undeclared " +
                $"fields: {string.Join(", ", extra)}");
        return new DocPackageDocument(templateId, payload);
    }

    private static JsonObject LePayload(string templateId,
        ApplicationDocument doc, RateLockRecord? lock_,
        FeeBreakdownDto? fees)
    {
        if (lock_ is null)
            throw DomainException.Conflict(
                "le-package requires an active rate lock");
        if (fees is null || fees.Status != "done")
            throw DomainException.Conflict(
                "le-package requires a completed fee evaluation");

        var amount = Num(doc, "loan.base_amount")
            ?? Num(doc, "loan_amount");
        var term = Int(doc, "loan_term_months");
        var names = BorrowerNames(doc);
        var address = PropertyAddress(doc);
        var pi = MonthlyPI(amount, lock_.Rate, term);
        var sections = fees.Sections.Select(s => new JsonObject
        {
            ["section"] = s.Section,
            ["amount"] = s.EffectiveTotal
        }).ToList();

        return templateId switch
        {
            "loan-estimate" => new JsonObject
            {
                ["borrowerNames"] = Arr(names),
                ["propertyAddress"] = address,
                ["salesPrice"] = Num(doc, "subject_property.purchase_price"),
                ["estimatedValue"] = Num(doc, "subject_property.est_value"),
                ["loanAmount"] = amount,
                ["loanType"] = Str(doc, "loan.product_type"),
                ["loanPurpose"] = Str(doc, "loan_purpose"),
                ["productCode"] = lock_.ProductCode,
                ["termMonths"] = term,
                ["interestRate"] = lock_.Rate,
                ["monthlyPrincipalInterest"] = pi,
                ["rateLockExpiresUtc"] = lock_.ExpiresUtc,
                ["closingCosts"] = new JsonArray(
                    sections.Cast<JsonNode>().ToArray()),
                ["estimatedCashToClose"] = CashToClose(doc, fees)
            },
            "initial-disclosures" => new JsonObject
            {
                ["borrowerNames"] = Arr(names),
                ["propertyAddress"] = address,
                ["loanAmount"] = amount,
                ["loanType"] = Str(doc, "loan.product_type"),
                ["termMonths"] = term,
                ["interestRate"] = lock_.Rate,
                ["intentToOccupy"] = Str(doc, "declaration.intent_to_occupy"),
                ["disclosureDate"] = DateTimeOffset.UtcNow
                    .ToString("yyyy-MM-dd"),
                ["lenderName"] = "LOLA Mortgage"
            },
            "1003-summary" => new JsonObject
            {
                ["applicant"] = Applicant(doc),
                ["loanAmount"] = amount,
                ["loanType"] = Str(doc, "loan.product_type"),
                ["loanPurpose"] = Str(doc, "loan_purpose"),
                ["termMonths"] = term,
                ["propertyAddress"] = address,
                ["estimatedValue"] = Num(doc, "subject_property.est_value"),
                ["salesPrice"] = Num(doc, "subject_property.purchase_price"),
                ["employment"] = Employment(doc)
            },
            _ => throw DomainException.BadRequest(
                $"template '{templateId}' is not part of le-package")
        };
    }

    private static JsonObject AdversePayload(string applicationId,
        ApplicationDocument doc, IReadOnlyList<string>? reasonCodes)
    {
        if (reasonCodes is not { Count: > 0 })
            throw DomainException.BadRequest(
                "adverse-action requires at least one reason code");
        var unknown = reasonCodes
            .Where(r => !ReasonTexts.ContainsKey(r)).ToList();
        if (unknown.Count > 0)
            throw DomainException.BadRequest(
                $"unknown reason codes: {string.Join(", ", unknown)}");

        var names = BorrowerNames(doc);
        return new JsonObject
        {
            ["applicantName"] = names.FirstOrDefault() ?? "Applicant",
            ["applicationId"] = applicationId,
            ["noticeDate"] = DateTimeOffset.UtcNow.ToString("yyyy-MM-dd"),
            ["reasons"] = new JsonArray(reasonCodes
                .Select(r => (JsonNode)ReasonTexts[r]).ToArray()),
            ["lenderName"] = "LOLA Mortgage"
        };
    }

    // --- payload pieces -------------------------------------------------

    private static List<string> BorrowerNames(ApplicationDocument doc)
    {
        var names = new List<string>();
        for (var i = 0; ; i++)
        {
            var first = Str(doc, $"party[{i}].first_name");
            var last = Str(doc, $"party[{i}].last_name");
            if (first is null && last is null) break;
            var middle = Str(doc, $"party[{i}].middle_name");
            names.Add(string.Join(' ',
                new[] { first, middle, last }
                    .Where(s => !string.IsNullOrWhiteSpace(s))));
        }
        return names;
    }

    private static JsonObject Applicant(ApplicationDocument doc)
        => new()
        {
            ["name"] = BorrowerNames(doc).FirstOrDefault(),
            ["email"] = Str(doc, "party[0].email"),
            ["phone"] = Str(doc, "party[0].phone"),
            ["currentAddress"] = string.Join(", ",
                new[]
                {
                    Str(doc, "party[0].address[0].street"),
                    Str(doc, "party[0].address[0].unit"),
                    Str(doc, "party[0].address[0].city"),
                    Str(doc, "party[0].address[0].state"),
                    Str(doc, "party[0].address[0].zip")
                }.Where(s => !string.IsNullOrWhiteSpace(s)))
        };

    private static string? PropertyAddress(ApplicationDocument doc)
        => string.Join(", ", new[]
        {
            Str(doc, "subject_property.street"),
            Str(doc, "subject_property.city"),
            Str(doc, "subject_property.state"),
            Str(doc, "subject_property.zip")
        }.Where(s => !string.IsNullOrWhiteSpace(s))) is { Length: > 0 } a
            ? a : null;

    private static JsonArray Employment(ApplicationDocument doc)
    {
        var rows = new List<JsonObject>();
        for (var i = 0; ; i++)
        {
            var p = $"party[0].employment[{i}]";
            var employer = Str(doc, $"{p}.employer_name");
            if (employer is null) break;
            rows.Add(new JsonObject
            {
                ["employer"] = employer,
                ["selfEmployed"] = Bool(doc, $"{p}.self_employed")
            });
        }
        return new JsonArray(rows.Cast<JsonNode>().ToArray());
    }

    private static decimal? CashToClose(ApplicationDocument doc,
        FeeBreakdownDto fees)
    {
        var price = Num(doc, "subject_property.purchase_price")
            ?? Num(doc, "subject_property.est_value");
        var amount = Num(doc, "loan.base_amount")
            ?? Num(doc, "loan_amount");
        return price is null || amount is null ? null
            : price.Value - amount.Value + fees.EffectiveTotal;
    }

    private static decimal? MonthlyPI(decimal? principal,
        decimal annualRatePct, int? termMonths)
    {
        if (principal is null || termMonths is null or 0) return null;
        var r = (double)annualRatePct / 1200.0;
        var n = (double)termMonths.Value;
        var p = (double)principal.Value;
        var pay = r == 0 ? p / n
            : p * r * Math.Pow(1 + r, n) / (Math.Pow(1 + r, n) - 1);
        return (decimal)Math.Round(pay, 2);
    }

    // --- doc readers (same convention as ScenarioMapper) -----------------

    private static JsonArray Arr(IEnumerable<string> items)
        => new(items.Select(i => (JsonNode)i).ToArray());

    private static string? Str(ApplicationDocument d, string path)
        => d.Get(path) is { } n
            ? n is JsonValue v && v.TryGetValue<string>(out var s)
                ? s : n.ToString()
            : null;

    private static decimal? Num(ApplicationDocument d, string path)
        => decimal.TryParse(Str(d, path), out var v) ? v : null;

    private static int? Int(ApplicationDocument d, string path)
        => decimal.TryParse(Str(d, path), out var v)
            ? (int)Math.Round(v) : null;

    private static bool? Bool(ApplicationDocument d, string path)
        => Str(d, path)?.Trim().ToLowerInvariant() switch
        {
            "true" or "yes" or "y" or "1" => true,
            "false" or "no" or "n" or "0" => false,
            _ => null
        };
}
