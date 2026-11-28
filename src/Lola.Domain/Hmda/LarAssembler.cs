namespace Lola.Domain.Hmda;

/// Inputs the wiring layer extracts from the application document,
/// latest ATR evaluation, and workflow state. Everything nullable —
/// missing inputs assemble as explicit NA codes per the FIG, never
/// invented values. All enum values are the catalog vocabulary; the
/// assembler maps them to FIG numeric codes.
public sealed record LarInput(
    string Lei,
    string UliSuffix,
    // doc strings (already-extracted doc paths)
    string? ApplicationDate,
    string? LoanType,          // conventional|fha|va|usda_rhs — from loan.product_type
    string? LoanPurpose,       // purchase|home_improvement|refinance|cash_out_refinance|other
    string? ConstructionMethod,
    string? OccupancyType,     // principal_residence|second_residence|investment
    decimal? LoanAmount,
    string? ActionTaken,
    string? ActionTakenDate,
    string? StreetAddress, string? City, string? State, string? Zip, string? CensusTract,
    string? Ethnicity1, string? Ethnicity2,
    string? Race1, string? Race2,
    string? Sex,
    string? CollectionMethod,  // collected visual/surname vs not
    DateOnly? Dob,
    DateOnly? AsOfDate,        // age computation reference (action date or today)
    decimal? Income,           // gross annual income relied upon, dollars
    decimal? InterestRate,
    decimal? PrepaymentPenaltyTermMonths,
    decimal? DtiPercent,
    decimal? CltvPercent,
    decimal? LoanTermMonths,
    decimal? IntroRatePeriodMonths,
    string? BalloonPayment, string? InterestOnlyPayment, string? NegativeAmortization,
    string? OtherNonamortizing,
    decimal? PropertyValue,
    string? ManufacturedSecuredType, string? ManufacturedLandInterest,
    decimal? TotalUnits, decimal? MultifamilyAffordableUnits,
    string? SubmissionOfApplication, string? InitiallyPayableTo,
    string? NmlsId,
    string? ReverseMortgage, string? OpenEndLineOfCredit, string? BusinessOrCommercial,
    // ATR-derived
    decimal? Apr, decimal? Apor, bool? HoepaHighCost,
    decimal? OriginationCharges, decimal? DiscountPoints, decimal? LenderCredits,
    // M25: latest AUS submission per vendor — null until an
    // automated-underwriting result exists (columns stay NA)
    string? AusVendor = null,
    string? AusRecommendation = null);

public static class LarAssembler
{
    public const string Na = "NA";

    /// Assembles the keyed LAR record: column key → FIG-formatted
    /// string value. Every ExportColumns key is present exactly once.
    public static IReadOnlyDictionary<string, string> Assemble(LarInput i)
    {
        var r = new Dictionary<string, string>
        {
            ["record_identifier"] = "2",
            ["lei"] = i.Lei,
            ["uli"] = Uli.Generate(i.Lei, i.UliSuffix),
            ["application_date"] = Date(i.ApplicationDate),
            ["loan_type"] = Enum(i.LoanType, LoanTypeMap),
            ["loan_purpose"] = Enum(i.LoanPurpose, LoanPurposeMap),
            ["preapproval"] = "2",  // preapproval program not modeled — 2 = not requested
            ["construction_method"] = Enum(i.ConstructionMethod, ConstructionMap),
            ["occupancy_type"] = Enum(i.OccupancyType, OccupancyMap),
            ["loan_amount"] = Dec(i.LoanAmount),
            ["action_taken"] = Enum(i.ActionTaken, ActionTakenMap),
            ["action_taken_date"] = Date(i.ActionTakenDate),
            ["street_address"] = Or(i.StreetAddress, Na),
            ["city"] = Or(i.City, Na),
            ["state"] = Or(i.State, Na),
            ["zip_code"] = Or(i.Zip, Na),
            ["census_tract"] = Or(i.CensusTract, Na),
            ["ethnicity_1"] = Enum(i.Ethnicity1, EthnicityMap),
            ["ethnicity_2"] = Enum(i.Ethnicity2, EthnicityMap2),
            ["ethnicity_3"] = "",
            ["ethnicity_4"] = "",
            ["ethnicity_5"] = "",
            ["ethnicity_freeform"] = "",
            ["ethnicity_visual_surname"] = VisualSurname(i.CollectionMethod),
            ["race_1"] = Enum(i.Race1, RaceMap),
            ["race_2"] = Enum(i.Race2, RaceMap),
            ["race_3"] = "",
            ["race_4"] = "",
            ["race_5"] = "",
            ["race_freeform_native"] = "",
            ["race_freeform_asian"] = "",
            ["race_freeform_islander"] = "",
            ["race_visual_surname"] = VisualSurname(i.CollectionMethod),
            ["sex"] = Enum(i.Sex, SexMap),
            ["sex_visual_surname"] = VisualSurname(i.CollectionMethod),
            ["age"] = Age(i.Dob, i.AsOfDate),
            ["income"] = i.Income is null ? Na : ((int)Math.Round(i.Income.Value / 1000m)).ToString(),
            ["type_of_purchaser"] = "0",  // not sold / not applicable in posture scope
            ["rate_of_interest"] = Dec(i.InterestRate),
            ["prepayment_penalty_term"] = Dec(i.PrepaymentPenaltyTermMonths),
            ["debt_to_income_ratio"] = i.DtiPercent is null ? Na : i.DtiPercent.Value.ToString("0.##"),
            ["combined_loan_to_value"] = i.CltvPercent is null ? Na : i.CltvPercent.Value.ToString("0.##"),
            ["loan_term"] = i.LoanTermMonths is null ? Na : ((int)i.LoanTermMonths.Value).ToString(),
            ["introductory_rate_period"] = i.IntroRatePeriodMonths is null ? Na : ((int)i.IntroRatePeriodMonths.Value).ToString(),
            ["balloon_payment"] = Enum(i.BalloonPayment, YesNoMap),
            ["interest_only_payments"] = Enum(i.InterestOnlyPayment, YesNoMap),
            ["negative_amortization"] = Enum(i.NegativeAmortization, YesNoMap),
            ["other_nonamortizing_features"] = Enum(i.OtherNonamortizing, YesNoMap),
            ["property_value"] = Dec(i.PropertyValue),
            ["manufactured_home_secured_property_type"] = Enum(i.ManufacturedSecuredType, ManufacturedSecuredMap),
            ["manufactured_home_land_property_interest"] = Enum(i.ManufacturedLandInterest, ManufacturedLandMap),
            ["total_units"] = i.TotalUnits is null ? Na : ((int)i.TotalUnits.Value).ToString(),
            ["multifamily_affordable_units"] = i.MultifamilyAffordableUnits is null ? Na : ((int)i.MultifamilyAffordableUnits.Value).ToString(),
            ["submission_of_application"] = Enum(i.SubmissionOfApplication, SubmissionMap),
            ["initially_payable"] = Enum(i.InitiallyPayableTo, PayableMap),
            ["nmlsr_identifier"] = Or(i.NmlsId, Na),
            ["aus_1"] = AusSystemCode(i.AusVendor) ?? Na,
            ["aus_2"] = "",
            ["aus_3"] = "",
            ["aus_4"] = "",
            ["aus_5"] = "",
            ["aus_freeform"] = "",
            ["aus_result_1"] = AusResultCode(i.AusVendor, i.AusRecommendation) ?? Na,
            ["aus_result_2"] = "",
            ["aus_result_3"] = "",
            ["aus_result_4"] = "",
            ["aus_result_5"] = "",
            ["aus_result_freeform"] = "",
            ["reverse_mortgage"] = Enum(i.ReverseMortgage, YesNoNaMap),
            ["open_end_line_of_credit"] = Enum(i.OpenEndLineOfCredit, YesNoNaMap),
            ["business_or_commercial_purpose"] = Enum(i.BusinessOrCommercial, YesNoMap),
            ["hoepa_status"] = i.HoepaHighCost is null ? Na : i.HoepaHighCost.Value ? "1" : "2",
            ["rate_spread"] = RateSpread(i.Apr, i.Apor),
            ["loan_costs_origination_charges"] = Dec(i.OriginationCharges),
            ["loan_costs_discount_points"] = Dec(i.DiscountPoints),
            ["loan_costs_lender_credits"] = Dec(i.LenderCredits),
            ["total_loan_costs_or_points_and_fees"] = Na,
        };
        return r;
    }

    /// Rate spread per 12 CFR 1003.4(a)(12): APR − APOR, reported to
    /// two decimals; NA when either side is absent.
    private static string RateSpread(decimal? apr, decimal? apor) =>
        apr is null || apor is null ? Na : (apr.Value - apor.Value).ToString("0.00");

    private static string Age(DateOnly? dob, DateOnly? asOf)
    {
        if (dob is null) return Na;
        var reference = asOf ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var age = reference.Year - dob.Value.Year;
        if (reference < dob.Value.AddYears(age)) age--;
        return age.ToString();
    }

    private static string VisualSurname(string? collectionMethod) =>
        collectionMethod switch
        {
            "visual_observation_or_surname" => "1",
            null or "" => Na,
            _ => "2",
        };

    private static string Date(string? iso) =>
        string.IsNullOrWhiteSpace(iso) ? Na
        : DateOnly.TryParse(iso, out var d) ? d.ToString("yyyyMMdd")
        : Na;

    private static string Dec(decimal? v) => v is null ? Na : v.Value.ToString("0.##");
    private static string Or(string? v, string fallback) => string.IsNullOrWhiteSpace(v) ? fallback : v;

    private static string Enum(string? v, IReadOnlyDictionary<string, string> map) =>
        v is not null && map.TryGetValue(v, out var code) ? code : Na;

    // Catalog vocabulary → FIG numeric codes.
    private static readonly IReadOnlyDictionary<string, string> LoanTypeMap = new Dictionary<string, string>
    { ["conventional"] = "1", ["fha"] = "2", ["va"] = "3", ["usda_rhs"] = "4" };
    private static readonly IReadOnlyDictionary<string, string> LoanPurposeMap = new Dictionary<string, string>
    { ["purchase"] = "1", ["home_improvement"] = "2", ["refinance"] = "31", ["cash_out_refinance"] = "32", ["other"] = "4" };
    private static readonly IReadOnlyDictionary<string, string> ConstructionMap = new Dictionary<string, string>
    { ["site_built"] = "1", ["manufactured_home"] = "2" };
    private static readonly IReadOnlyDictionary<string, string> OccupancyMap = new Dictionary<string, string>
    {
        ["principal_residence"] = "1",
        ["second_residence"] = "2",
        ["investment"] = "3",
        ["primary_residence"] = "1",
        ["second_home"] = "2",
        ["investment_property"] = "3"
    };
    private static readonly IReadOnlyDictionary<string, string> ActionTakenMap = new Dictionary<string, string>
    {
        ["originated"] = "1",
        ["approved_not_accepted"] = "2",
        ["denied"] = "3",
        ["withdrawn"] = "4",
        ["incomplete"] = "5",
        ["purchased"] = "6",
        ["preapproval_denied"] = "7",
        ["preapproval_approved_not_accepted"] = "8"
    };
    private static readonly IReadOnlyDictionary<string, string> EthnicityMap = new Dictionary<string, string>
    {
        ["hispanic_or_latino"] = "1",
        ["mexican"] = "11",
        ["puerto_rican"] = "12",
        ["cuban"] = "13",
        ["other_hispanic_or_latino"] = "14",
        ["not_hispanic_or_latino"] = "2",
        ["not_provided"] = "3",
        ["not_applicable"] = "4"
    };
    private static readonly IReadOnlyDictionary<string, string> EthnicityMap2 = new Dictionary<string, string>
    { ["mexican"] = "11", ["puerto_rican"] = "12", ["cuban"] = "13", ["other_hispanic_or_latino"] = "14" };
    private static readonly IReadOnlyDictionary<string, string> RaceMap = new Dictionary<string, string>
    {
        ["american_indian_or_alaska_native"] = "1",
        ["asian"] = "2",
        ["asian_indian"] = "21",
        ["chinese"] = "22",
        ["filipino"] = "23",
        ["japanese"] = "24",
        ["korean"] = "25",
        ["vietnamese"] = "26",
        ["other_asian"] = "27",
        ["black_or_african_american"] = "3",
        ["native_hawaiian_or_other_pacific_islander"] = "4",
        ["native_hawaiian"] = "41",
        ["guamanian_or_chamorro"] = "42",
        ["samoan"] = "43",
        ["other_pacific_islander"] = "44",
        ["white"] = "5",
        ["not_provided"] = "6",
        ["not_applicable"] = "7"
    };
    private static readonly IReadOnlyDictionary<string, string> SexMap = new Dictionary<string, string>
    { ["male"] = "1", ["female"] = "2", ["not_provided"] = "3", ["not_applicable"] = "4" };
    private static readonly IReadOnlyDictionary<string, string> YesNoMap = new Dictionary<string, string>
    { ["yes"] = "1", ["no"] = "2" };
    private static readonly IReadOnlyDictionary<string, string> YesNoNaMap = new Dictionary<string, string>
    { ["yes"] = "1", ["no"] = "2", ["not_applicable"] = "1111" };
    private static readonly IReadOnlyDictionary<string, string> ManufacturedSecuredMap = new Dictionary<string, string>
    { ["manufactured_home_and_land"] = "1", ["manufactured_home_not_land"] = "2", ["not_applicable"] = "3" };
    private static readonly IReadOnlyDictionary<string, string> ManufacturedLandMap = new Dictionary<string, string>
    {
        ["direct_ownership"] = "1",
        ["indirect_ownership"] = "2",
        ["paid_leasehold"] = "3",
        ["unpaid_leasehold"] = "4",
        ["not_applicable"] = "5"
    };
    private static readonly IReadOnlyDictionary<string, string> SubmissionMap = new Dictionary<string, string>
    { ["directly"] = "1", ["not_directly"] = "2", ["not_applicable"] = "1111" };
    private static readonly IReadOnlyDictionary<string, string> PayableMap = new Dictionary<string, string>
    { ["your_institution"] = "1", ["not_your_institution"] = "2", ["not_applicable"] = "1111" };

    /// FIG 2024 AUS enumerations: system 1=DU, 2=LPA (LP), 3=TOTAL,
    /// 4=GUS, 5=Other. Result codes per FIG appendix: DU outcomes use
    /// 1/3/4/5/13-family; LPA uses 8/9/10-family. Generic mappings
    /// documented — vendor-specific certification may refine.
    internal static string? AusSystemCode(string? vendor) => vendor switch
    {
        "du" => "1",
        "lpa" => "2",
        _ => null,
    };

    internal static string? AusResultCode(string? vendor, string? recommendation)
        => (vendor, recommendation) switch
        {
            ("du", "approve-eligible") => "1",   // Approve/Eligible
            ("du", "refer") => "13",             // Refer
            ("du", "ineligible") => "10",        // Ineligible
            ("lpa", "accept") => "8",            // Accept
            ("lpa", "caution") => "9",           // Caution
            ("lpa", "refer") => "13",            // Refer
            ("lpa", "ineligible") => "10",       // Ineligible
            (_, "unable-to-evaluate") => "15",   // Unable to Determine
            _ => null,
        };
}
