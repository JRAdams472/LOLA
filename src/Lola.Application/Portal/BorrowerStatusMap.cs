namespace Lola.Application.Portal;

/// Internal vocabulary → borrower vocabulary (M12 acceptance: "no
/// internal status values in borrower API responses"). The map is
/// total — every known status lands on a stage, and an unrecognized
/// value falls back to the first stage rather than leaking through.
public static class BorrowerStatusMap
{
    /// The ordered tracker stages the UI renders.
    public static readonly IReadOnlyList<(string Key, string Label)> Stages =
    [
        ("applying", "Your application"),
        ("review", "Under review"),
        ("decision", "Decision"),
    ];

    /// status/pipeline_step → (label, stageIndex).
    public static (string Label, int Stage) Map(string status)
        => status switch
        {
            "draft" => ("Application in progress", 0),
            "submitted" => ("Under review", 1),
            "in_review" => ("Under review", 1),
            "decisioned" => ("Decision made", 2),
            _ => ("Your application", 0),
        };

    public static IReadOnlyList<PortalStageDto> StageList(int current)
        => Stages.Select((s, i) => new PortalStageDto(
            s.Key, s.Label, i < current, i == current)).ToList();

    /// Vault scan status → borrower language. The raw quarantine
    /// vocabulary (`quarantined`/`infected`) stays inside the vault.
    public static (string Key, string Label) UploadStatus(string status)
        => status switch
        {
            "clean" => ("received", "Received"),
            "infected" => ("rejected",
                "We couldn't accept this file — please upload a different one"),
            _ => ("checking", "Checking…"),
        };

    /// Registry doc status → borrower language + download readiness.
    public static (string Label, bool Ready) DocumentStatus(string status)
        => status switch
        {
            "completed" => ("Ready", true),
            "failed" => ("Not available", false),
            _ => ("Preparing…", false),
        };

    /// Package kind → borrower language.
    public static string DocumentKindLabel(string kind)
        => kind switch
        {
            "le-package" => "Loan Estimate package",
            _ => "Document package",
        };
}
