using System.Xml.Linq;

namespace Lola.Domain.Aus;

/// DU findings-response XML -> normalized result. Malformed input returns
/// UnableToEvaluate with a fatal finding - typed failure, never a throw.
public static class DuResponseParser
{
    public static AusResult Parse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var recText = FindValue(doc, "Recommendation") ?? FindValue(doc, "UnderwritingRecommendation");
            var caseId = FindValue(doc, "CaseFileIdentifier") ?? FindValue(doc, "CasefileIdentifier");
            var findings = doc.Descendants()
                .Where(e => e.Name.LocalName is "DU_MESSAGE" or "DuMessage" or "FINDING")
                .Select(e => new AusFinding(
                    FindValue(e, "MessageCode") ?? FindValue(e, "Code") ?? "DU-UNK",
                    AusNormalizer.ClassifyCategory(FindValue(e, "Category")),
                    AusNormalizer.ClassifySeverity(FindValue(e, "Severity")),
                    FindValue(e, "Message") ?? FindValue(e, "MessageText") ?? ""))
                .ToList();
            return new AusResult("du", MapRecommendation(recText), FindValue(doc, "RiskClassification"), caseId, findings);
        }
        catch
        {
            return new AusResult("du", AusRecommendation.UnableToEvaluate, null, null,
                [new AusFinding("DU-MALFORMED", AusFindingCategory.Other, AusFindingSeverity.Fatal, "Unparseable DU response")]);
        }
    }

    internal static AusRecommendation MapRecommendation(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "APPROVE/ELIGIBLE" or "APPROVE" or "ELIGIBLE" => AusRecommendation.ApproveEligible,
        "REFER" or "REFER WITH CAUTION" or "REFER/ELIGIBLE" => AusRecommendation.Refer,
        "INELIGIBLE" or "OUT OF SCOPE" => AusRecommendation.Ineligible,
        _ => AusRecommendation.UnableToEvaluate,
    };

    internal static string? FindValue(XElement el, string localName)
        => el.Descendants().FirstOrDefault(d => d.Name.LocalName == localName)?.Value is { Length: > 0 } v ? v : null;

    internal static string? FindValue(XDocument doc, string localName)
        => doc.Descendants().FirstOrDefault(d => d.Name.LocalName == localName)?.Value is { Length: > 0 } v ? v : null;
}

/// LPA feedback-certificate XML -> normalized result.
public static class LpaResponseParser
{
    public static AusResult Parse(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var riskClass = DuResponseParser.FindValue(doc, "RiskClass");
            var findings = doc.Descendants()
                .Where(e => e.Name.LocalName is "Message" or "FEEDBACK_MESSAGE" or "FeedbackMessage")
                .Select(e => new AusFinding(
                    DuResponseParser.FindValue(e, "Code") ?? "LPA-UNK",
                    AusNormalizer.ClassifyCategory(DuResponseParser.FindValue(e, "Category")),
                    AusNormalizer.ClassifySeverity(DuResponseParser.FindValue(e, "Severity")),
                    DuResponseParser.FindValue(e, "MessageText") ?? e.Value.Trim()))
                .ToList();
            return new AusResult("lpa", MapRecommendation(riskClass ?? DuResponseParser.FindValue(doc, "Recommendation")), riskClass,
                DuResponseParser.FindValue(doc, "LpaKey") ?? DuResponseParser.FindValue(doc, "LoanKey"), findings);
        }
        catch
        {
            return new AusResult("lpa", AusRecommendation.UnableToEvaluate, null, null,
                [new AusFinding("LPA-MALFORMED", AusFindingCategory.Other, AusFindingSeverity.Fatal, "Unparseable LPA response")]);
        }
    }

    internal static AusRecommendation MapRecommendation(string? text) => text?.Trim().ToUpperInvariant() switch
    {
        "ACCEPT" or "ACCEPTPLUS" or "ACCEPT PLUS" => AusRecommendation.Accept,
        "CAUTION" => AusRecommendation.Caution,
        "REFER" => AusRecommendation.Refer,
        "INELIGIBLE" => AusRecommendation.Ineligible,
        _ => AusRecommendation.UnableToEvaluate,
    };
}
