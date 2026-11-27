namespace Lola.Domain.Hmda;

/// The canonical pipe-delimited LAR column order per FIG Table 2
/// (stable since the 2018 collection year, unchanged through the 2026
/// FIG). Each entry is the data-point key the assembler populates; the
/// export layer joins values in exactly this order.
///
/// Scope note (per docs/m24-plan.md): AUS columns assemble as NA until
/// M25 lands. Free-form text columns are emitted blank unless the doc
/// carries them — they are conditional, not required.
public static class LarSchema
{
    public static readonly IReadOnlyList<string> Columns =
    [
        "record_identifier",
        "lei",
        "uli",
        "application_date",
        "loan_type",
        "loan_purpose",
        "preapproval",
        "construction_method",
        "occupancy_type",
        "loan_amount",
        "action_taken",
        "action_taken_date",
        "street_address",
        "city",
        "state",
        "zip_code",
        "census_tract",
        "ethnicity_1", "ethnicity_2", "ethnicity_3", "ethnicity_4", "ethnicity_5",
        "ethnicity_freeform",
        "ethnicity_visual_surname",
        "race_1", "race_2", "race_3", "race_4", "race_5",
        "race_freeform_native", "race_freeform_asian", "race_freeform_islander",
        "race_visual_surname",
        "sex",
        "sex_visual_surname",
        "age",
        "income",
        "type_of_purchaser",
        "rate_of_interest",
        "prepayment_penalty_term",
        "debt_to_income_ratio",
        "combined_loan_to_value",
        "loan_term",
        "introductory_rate_period",
        "balloon_payment",
        "interest_only_payments",
        "negative_amortization",
        "other_nonamortizing_features",
        "property_value",
        "manufactured_home_secured_property_type",
        "manufactured_home_land_property_interest",
        "total_units",
        "multifamily_affordable_units",
        "submission_of_application",
        "initially_payable",
        "nmlsr_identifier",
        "aus_1", "aus_2", "aus_3", "aus_4", "aus_5",
        "aus_freeform",
        "aus_result_1", "aus_result_2", "aus_result_3", "aus_result_4", "aus_result_5",
        "aus_result_freeform",
        "reverse_mortgage",
        "open_end_line_of_credit",
        "business_or_commercial_purpose",
        "hoepa_status",
        "rate_spread",
        "loan_costs_origination_charges",
        "loan_costs_discount_points",
        "loan_costs_lender_credits",
        "total_loan_costs_or_points_and_fees",
    ];

    /// The export layout = Columns in order. Co-applicant demographic
    /// columns (below) are inserted after `age` when a co-applicant
    /// exists, matching the FIG's contiguous applicant/co-applicant
    /// demographics block.
    public static readonly IReadOnlyList<string> ExportColumns = Columns;

    /// Co-applicant demographic columns inserted after the applicant
    /// demographics when party[1] exists — FIG orders all applicant
    /// then co-applicant demographics contiguously; this list models
    /// the co-applicant block.
    public static readonly IReadOnlyList<string> CoApplicantColumns =
    [
        "co_ethnicity_1", "co_ethnicity_2", "co_ethnicity_3", "co_ethnicity_4", "co_ethnicity_5",
        "co_ethnicity_freeform", "co_ethnicity_visual_surname",
        "co_race_1", "co_race_2", "co_race_3", "co_race_4", "co_race_5",
        "co_race_freeform_native", "co_race_freeform_asian", "co_race_freeform_islander",
        "co_race_visual_surname",
        "co_sex", "co_sex_visual_surname", "co_age",
    ];
}
