using Lola.Domain.Applications;
using Lola.Domain.Hmda;

namespace Lola.Application.Compliance;

/// Maps application state to the LAR assembler's inputs (M24 P2,
/// MPE-313): doc paths inside the cipher boundary supply the HMDA
/// collection fields; the latest ATR evaluation supplies rate-spread
/// and HOEPA inputs; workflow status supplies nothing yet — action
/// taken is officer-collected via the `hmda.*` catalog fields.
/// Missing inputs stay null — the assembler emits explicit NA codes.
public static class HmdaEvaluator
{
    /// ULI suffix from the application id: uppercase alphanumeric only
    /// (FIG constraint), max 23 chars, no borrower-identifying content
    /// — application ids are system-generated opaque identifiers.
    public static string UliSuffix(string appId)
    {
        var clean = new string(appId.ToUpperInvariant()
            .Where(c => char.IsDigit(c) || c is >= 'A' and <= 'Z')
            .ToArray());
        return clean.Length <= 23 ? clean : clean[..23];
    }

    public static LarInput Map(ApplicationDocument doc, string lei,
        string appId, AtrResultRecord? latestAtr, string? nmlsId)
        => new(
            Lei: lei,
            UliSuffix: UliSuffix(appId),
            ApplicationDate: Str(doc, "hmda.application_date"),
            LoanType: Str(doc, "loan.product_type"),
            LoanPurpose: Str(doc, "loan_purpose"),
            ConstructionMethod: Str(doc, "subject_property.construction_method"),
            OccupancyType: Str(doc, "subject_property.usage"),
            LoanAmount: Num(doc, "loan_amount"),
            ActionTaken: Str(doc, "hmda.action_taken"),
            ActionTakenDate: Str(doc, "hmda.action_taken_date"),
            StreetAddress: Str(doc, "subject_property.street"),
            City: Str(doc, "subject_property.city"),
            State: Str(doc, "subject_property.state"),
            Zip: Str(doc, "subject_property.zip"),
            CensusTract: Str(doc, "subject_property.census_tract"),
            Ethnicity1: Str(doc, "demographics.ethnicity_1")
                ?? Str(doc, "demographics.ethnicity"),
            Ethnicity2: Str(doc, "demographics.ethnicity_2"),
            Race1: Str(doc, "demographics.race_1"),
            Race2: Str(doc, "demographics.race_2"),
            Sex: Str(doc, "demographics.sex_detail")
                ?? Str(doc, "demographics.sex"),
            CollectionMethod: Str(doc, "demographics.collection_method"),
            Dob: DateOnly.TryParse(Str(doc, "party[0].dob"), out var dob) ? dob : null,
            AsOfDate: null,
            Income: Num(doc, "hmda.income"),
            InterestRate: Num(doc, "interest_rate"),
            PrepaymentPenaltyTermMonths: Num(doc, "loan.prepayment_penalty_term_months"),
            DtiPercent: (decimal?)latestAtr?.Dti,
            CltvPercent: Num(doc, "loan.cltv"),
            LoanTermMonths: Num(doc, "loan_term_months"),
            IntroRatePeriodMonths: Num(doc, "loan.intro_rate_period_months"),
            BalloonPayment: Str(doc, "loan.balloon_payment"),
            InterestOnlyPayment: Str(doc, "loan.interest_only_payment"),
            NegativeAmortization: Str(doc, "loan.negative_amortization"),
            OtherNonamortizing: Str(doc, "loan.other_nonamortizing_features"),
            PropertyValue: Num(doc, "subject_property.est_value"),
            ManufacturedSecuredType: Str(doc, "subject_property.manufactured_home_secured_property_type"),
            ManufacturedLandInterest: Str(doc, "subject_property.manufactured_home_land_property_interest"),
            TotalUnits: Num(doc, "subject_property.units"),
            MultifamilyAffordableUnits: Num(doc, "subject_property.multifamily_affordable_units"),
            SubmissionOfApplication: Str(doc, "hmda.submission_of_application"),
            InitiallyPayableTo: Str(doc, "hmda.initially_payable_to"),
            NmlsId: nmlsId,
            ReverseMortgage: Str(doc, "loan.reverse_mortgage"),
            OpenEndLineOfCredit: Str(doc, "loan.open_end_line_of_credit"),
            BusinessOrCommercial: Str(doc, "loan.business_or_commercial_purpose"),
            Apr: (decimal?)latestAtr?.Apr,
            Apor: (decimal?)latestAtr?.Apor,
            HoepaHighCost: HoepaHighCost(latestAtr),
            OriginationCharges: Num(doc, "loan.origination_charges"),
            DiscountPoints: Num(doc, "loan.discount_points"),
            LenderCredits: Num(doc, "loan.lender_credits"));

    /// HOEPA high-cost flag from the latest ATR verdicts JSON — null
    /// when there is no evaluation or the test never ran conclusive.
    private static bool? HoepaHighCost(AtrResultRecord? atr)
    {
        if (atr is null) return null;
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(atr.VerdictsJson);
            // verdicts json is an array of {test, verdict, detail}
            foreach (var el in json.RootElement.EnumerateArray())
            {
                var test = el.TryGetProperty("test", out var t) ? t.GetString() : null;
                if (test?.StartsWith("hoepa", StringComparison.OrdinalIgnoreCase) != true)
                    continue;
                var verdict = el.TryGetProperty("verdict", out var v) ? v.GetString() : null;
                return verdict switch
                {
                    "fail" => true,
                    "pass" => false,
                    _ => null,
                };
            }
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string? Str(ApplicationDocument d, string path)
    {
        var node = d.Get(path);
        if (node is null) return null;
        return node is System.Text.Json.Nodes.JsonValue v
            && v.TryGetValue<string>(out var s) ? s : node.ToJsonString();
    }

    private static decimal? Num(ApplicationDocument d, string path)
    {
        var s = Str(d, path);
        return s is not null && decimal.TryParse(s, out var n) ? n : null;
    }
}
