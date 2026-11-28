using System.Xml.Linq;

namespace Lola.Domain.Aus;

/// <summary>
/// DU Codified Findings File (CFF) XML → normalized result. The CFF shape is
/// attribute-based: recommendation/case data live on attributes of
/// Casefile/UnderwritingRecommendation, findings are Messages/Message rows.
/// Malformed input returns UnableToEvaluate with a fatal finding — typed
/// failure, never a throw.
/// </summary>
public static class DuResponseParser
{
    public static AusResult Parse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var casefile = doc.Descendants().FirstOrDefault(e => e.Name.LocalName == "Casefile")
                ?? throw new FormatException("no Casefile");
            var rec = casefile.Descendants().FirstOrDefault(e => e.Name.LocalName == "UnderwritingRecommendation");
            var findings = casefile.Descendants()
                .Where(e => e.Name.LocalName == "Message")
                .Select(e => new AusFinding(
                    Attr(e, "MessageIdentifier") ?? "DU-UNK",
                    AusNormalizer.ClassifyCategory(Attr(e, "MessageType")),
                    AusNormalizer.ClassifySeverity(Attr(e, "Severity")),
                    Attr(e, "Text") ?? ""))
                .ToList();
            return new AusResult("du",
                MapRecommendation(Attr(rec, "RecommendationType")),
                Attr(rec, "CreditRiskClassification"),
                Attr(casefile, "CasefileIdentifier"),
                findings);
        }
        catch
        {
            return new AusResult("du", AusRecommendation.UnableToEvaluate, null, null,
                [new AusFinding("DU-MALFORMED", AusFindingCategory.Other, AusFindingSeverity.Fatal, "Unparseable DU response")]);
        }
    }

    public static AusRecommendation MapRecommendation(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "APPROVEELIGIBLE" or "APPROVE/ELIGIBLE" or "APPROVE" or "ELIGIBLE" => AusRecommendation.ApproveEligible,
        "REFERWITHCAUTION" or "REFERWITHCAUTION1" or "REFERWITHCAUTION2" or "REFERWITHCAUTION3" or "REFERWITHCAUTION4"
            or "REFER WITH CAUTION" or "REFER" or "REFER/ELIGIBLE" => AusRecommendation.Refer,
        "INELIGIBLE" or "OUTOFSCOPE" or "OUT OF SCOPE" => AusRecommendation.Ineligible,
        _ => AusRecommendation.UnableToEvaluate,
    };

    internal static string? Attr(XElement? el, string name)
        => el?.Attribute(name)?.Value is { Length: > 0 } v ? v : null;

    internal static string? FindValue(XDocument doc, string localName)
        => doc.Descendants().FirstOrDefault(d => d.Name.LocalName == localName)?.Value is { Length: > 0 } v ? v : null;
}

/// <summary>
/// LPA feedback certificate XML → normalized result. Root is
/// LPALoanAssessmentServiceResponse wrapping a MISMO MESSAGE; recommendation
/// lives in LoanProspectorCreditRiskClassificationDescription, findings in
/// AUTOMATED_UNDERWRITING_SYSTEM_RESPONSE_MESSAGE_ITEM rows grouped by
/// AutomatedUnderwritingSystemMessageGroupType.
/// </summary>
public static class LpaResponseParser
{
    public static AusResult Parse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var riskClass = DuResponseParser.FindValue(doc, "LoanProspectorCreditRiskClassificationDescription")
                ?? DuResponseParser.FindValue(doc, "RiskClassificationDescription");
            var findings = doc.Descendants()
                .Where(e => e.Name.LocalName == "AUTOMATED_UNDERWRITING_SYSTEM_RESPONSE_MESSAGE_ITEM")
                .Select(e => new AusFinding(
                    ChildValue(e, "AutomatedUnderwritingSystemMessageSectionIdentifier") ?? "LPA-UNK",
                    AusNormalizer.ClassifyCategory(GroupOf(e)),
                    AusNormalizer.ClassifySeverity(null),
                    ChildValue(e, "AutomatedUnderwritingSystemMessageValue")
                        ?? ChildValue(e, "AutomatedUnderwritingSystemMessageDescription") ?? ""))
                .ToList();
            return new AusResult("lpa",
                MapRecommendation(riskClass),
                riskClass,
                DuResponseParser.FindValue(doc, "LPATransactionIdentifier"),
                findings);
        }
        catch
        {
            return new AusResult("lpa", AusRecommendation.UnableToEvaluate, null, null,
                [new AusFinding("LPA-MALFORMED", AusFindingCategory.Other, AusFindingSeverity.Fatal, "Unparseable LPA response")]);
        }
    }

    private static string? ChildValue(XElement parent, string localName)
        => parent.Descendants().FirstOrDefault(d => d.Name.LocalName == localName)?.Value is { Length: > 0 } v ? v : null;

    private static string? GroupOf(XElement item)
        => item.Ancestors().FirstOrDefault(a => a.Name.LocalName == "AUTOMATED_UNDERWRITING_SYSTEM_RESPONSE_MESSAGE_DETAIL")
            is { } detail ? ChildValue(detail, "AutomatedUnderwritingSystemMessageGroupType") : null;

    public static AusRecommendation MapRecommendation(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "ACCEPT" or "ACCEPTPLUS" or "ACCEPT PLUS" => AusRecommendation.Accept,
        "CAUTION" => AusRecommendation.Caution,
        "REFER" => AusRecommendation.Refer,
        "INELIGIBLE" => AusRecommendation.Ineligible,
        _ => AusRecommendation.UnableToEvaluate,
    };
}
