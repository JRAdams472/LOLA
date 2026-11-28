using System.Xml;
using System.Xml.Linq;

namespace Lola.Domain.Credit;

/// MISMO v2.3.1 credit-response parser (M26) — one bureau file per
/// document. Produces a typed `BureauFile`; malformed XML never throws
/// — it returns a failure result with zero tradelines and a null score
/// (the completion path records the typed failure, never a partial
/// silent merge).
///
/// Payment-history decode: `_PAYMENT_PATTERN` is a character grid whose
/// first character is the month of `_StartDate`, each subsequent
/// character stepping BACK one month; characters 1/2/3/4 are 30/60/90/
/// 120-day lates. Verified against the fixture's explicit adverse
/// rating dates (e.g. '3' at index 20 from a 2026-10 start = 2025-02,
/// matching the fixture's `Late90Days` adverse at 2025-02).
public static class CreditResponseParser
{
    private static readonly XmlReaderSettings SafeSettings = new()
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
    };

    public sealed record ParseOutcome(
        bool Ok, string? Error, BureauFile? File);

    internal static XDocument? ParseSafe(string xml, out string? error)
    {
        error = null;
        try
        {
            using var reader = XmlReader.Create(
                new StringReader(xml), SafeSettings);
            return XDocument.Load(reader);
        }
        catch (XmlException ex)
        {
            error = ex.Message;
            return null;
        }
    }

    public static ParseOutcome Parse(string xml, string? fallbackBureau)
    {
        var doc = ParseSafe(xml, out var error);
        if (doc is null)
            return new ParseOutcome(false, error ?? "malformed xml", null);

        var response = doc.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "CREDIT_RESPONSE");
        if (response is null)
            return new ParseOutcome(false, "no CREDIT_RESPONSE element", null);

        // Scores — first score wins per bureau file; model name tags
        // are preserved raw, never blended.
        var scoreEl = response.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "CREDIT_SCORE");
        int? score = null;
        string? model = null;
        string? bureau = fallbackBureau;
        if (scoreEl is not null)
        {
            if (int.TryParse(scoreEl.Attribute("_Value")?.Value, out var s))
                score = s;
            model = scoreEl.Attribute("_ModelNameTypeOtherDescription")?.Value
                ?? scoreEl.Attribute("_ModelNameType")?.Value;
            bureau ??= scoreEl.Attribute("CreditRepositorySourceType")?.Value;
        }
        bureau ??= response.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "CREDIT_REPOSITORY")
            ?.Attribute("_SourceType")?.Value
            ?? "unknown";
        bureau = bureau.ToLowerInvariant();

        var tradelines = new List<BureauTradeline>();
        foreach (var liab in response.Descendants()
            .Where(e => e.Name.LocalName == "CREDIT_LIABILITY"))
        {
            var creditor = liab.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "_CREDITOR")
                ?.Attribute("_Name")?.Value ?? "UNKNOWN";
            var mask = liab.Attribute("_AccountIdentifier")?.Value;
            var type = MapAccountType(
                liab.Attribute("_AccountType")?.Value);
            var balance = Dec(liab.Attribute("_UnpaidBalanceAmount")?.Value);
            var payment = Dec(liab.Attribute("_MonthlyPaymentAmount")?.Value);
            var reported = liab.Attribute("_AccountReportedDate")?.Value;
            var ratingType = liab.Descendants()
                .FirstOrDefault(e => e.Name.LocalName == "_CURRENT_RATING")
                ?.Attribute("_Type")?.Value;
            var status = MapRating(ratingType);
            var lates = DecodeAdverseRatings(liab);
            if (lates.Count == 0)
                lates = DecodePaymentPattern(liab);
            tradelines.Add(new BureauTradeline(
                creditor, mask, type, null, balance, payment, status,
                reported, lates));
        }

        return new ParseOutcome(true, null,
            new BureauFile(bureau, score, model, tradelines));
    }

    internal static decimal? Dec(string? raw)
        => decimal.TryParse(raw, out var v) ? v : null;

    internal static string MapAccountType(string? raw) =>
        raw?.ToLowerInvariant() switch
        {
            "mortgage" => "mortgage",
            "revolving" => "revolving",
            "installment" => "installment",
            "auto" or "automobile" => "auto",
            "student" or "education" => "student",
            null => "other",
            _ => "other",
        };

    /// Unknown rating vocabulary maps to null — never to "current":
    /// guessing current for an unrecognized code could mask a real
    /// delinquency in worst-status merging.
    public static string? MapRating(string? ratingType) =>
        ratingType?.ToLowerInvariant() switch
        {
            null or "asagreed" or "current" => "current",
            "late30days" => "30",
            "late60days" => "60",
            "late90days" => "90",
            "late120days" => "120",
            "collection" => "collection",
            "chargeoff" => "chargeoff",
            _ => null,
        };

    /// Explicit adverse ratings are the unambiguous delinquency
    /// source:  + 
    /// carry month + severity directly. Dedupe by month, worst
    /// severity wins. Preferred over the payment grid, whose
    /// month indexing varies by bureau implementation.
    internal static IReadOnlyList<LateEvent> DecodeAdverseRatings(
        XElement liab)
    {
        var events = new Dictionary<string, int>();
        foreach (var el in liab.Descendants().Where(e =>
            e.Name.LocalName is "_MOST_RECENT_ADVERSE_RATING"
                or "_PRIOR_ADVERSE_RATING"))
        {
            var month = el.Attribute("_Date")?.Value;
            var severity = el.Attribute("_Type")?.Value
                ?.ToLowerInvariant() switch
            {
                "late30days" => 30,
                "late60days" => 60,
                "late90days" => 90,
                "late120days" => 120,
                _ => 0,
            };
            if (month is null or "" || severity == 0) continue;
            if (!events.TryGetValue(month, out var existing)
                || severity > existing)
                events[month] = severity;
        }
        return events.Select(kv => new LateEvent(kv.Key, kv.Value))
            .ToList();
    }

    /// Decode the payment-pattern grid into month-tagged late events.
    /// char[0] = StartDate month; each subsequent char steps back one
    /// month. Only 1/2/3/4 characters produce events.
    internal static IReadOnlyList<LateEvent> DecodePaymentPattern(
        XElement liab)
    {
        var pattern = liab.Descendants()
            .FirstOrDefault(e => e.Name.LocalName == "_PAYMENT_PATTERN");
        if (pattern is null) return [];
        var data = pattern.Attribute("_Data")?.Value;
        var start = pattern.Attribute("_StartDate")?.Value;
        if (string.IsNullOrEmpty(data) || start is null or "") return [];
        if (!DateOnly.TryParse(start + "-01", out var startMonth))
            return [];
        var events = new List<LateEvent>();
        for (var i = 0; i < data.Length; i++)
        {
            var severity = data[i] switch
            {
                '1' => 30,
                '2' => 60,
                '3' => 90,
                '4' => 120,
                _ => 0,
            };
            if (severity == 0) continue;
            var month = startMonth.AddMonths(-i);
            events.Add(new LateEvent(month.ToString("yyyy-MM"), severity));
        }
        return events;
    }
}
