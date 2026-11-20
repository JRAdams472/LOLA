using System.Text;
using System.Text.RegularExpressions;
using Lola.Application.Vault;

namespace Lola.Infrastructure.Vault;

/// Declarative rule classifier (M18 P1) — a table of kind rules
/// scored against filename, declared classification, and a
/// strings-style text extraction of the captured head. Deterministic:
/// same input → same output, no AI, no decision engine. `DateField`
/// drives `expired_doc` two ways: `FreshWithinDays` (staleness —
/// paystubs, statements) or `ExpiryInFuture` (the doc's own expiry
/// date must still be ahead — IDs).
public sealed class RuleDocClassifier(TimeProvider clock)
    : IDocClassifier
{
    public string Adapter => "rule";

    /// Minimum text signal before a doc counts as readable — below
    /// this the head was binary (image scan) or empty.
    private const int MinTextChars = 64;
    private const int MaxTextChars = 256 * 1024;
    private const double FilenameBonus = 2.0;

    private sealed record Extractor(string Name, Regex Pattern,
        bool Required);
    private sealed record KindRule(string Kind, Regex[] Filename,
        (Regex Pattern, double Weight)[] Markers,
        Extractor[] Extractors, string? DateField,
        int FreshWithinDays, bool ExpiryInFuture);

    private static Regex Re(string p) => new(p,
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly KindRule[] Rules =
    [
        new(DocKind.W2,
            [Re(@"\bw[-_ ]?2\b"), Re(@"wage.{0,10}tax")],
            [(Re(@"wage and tax statement"), 3),
             (Re(@"form w-2"), 3),
             (Re(@"employer identification number"), 1.5),
             (Re(@"wages,\s*tips"), 1)],
            [new("employer_name",
                 Re(@"employer\s*name\s*[:#]?\s*(?<v>[A-Za-z0-9 &.,'-]{2,60})"), true),
             new("employer_address",
                 Re(@"employer\s*address\s*[:#]?\s*(?<v>[^\r\n]{5,80})"), false),
             new("tax_year",
                 Re(@"tax\s*year\s*[:#]?\s*(?<v>20\d{2})"), true),
             new("wages",
                 Re(@"wages[^0-9]{0,40}(?<v>\d[\d,]*\.\d{2})"), false)],
            DateField: null, FreshWithinDays: 0, ExpiryInFuture: false),

        new(DocKind.Paystub,
            [Re(@"pay\s*stub"), Re(@"paystub"), Re(@"earnings")],
            [(Re(@"pay\s*stub"), 3), (Re(@"earnings statement"), 3),
             (Re(@"gross pay"), 2), (Re(@"net pay"), 2),
             (Re(@"pay\s*period"), 1)],
            [new("employer_name",
                 Re(@"employer\s*name\s*[:#]?\s*(?<v>[A-Za-z0-9 &.,'-]{2,60})"), true),
             new("employer_address",
                 Re(@"employer\s*address\s*[:#]?\s*(?<v>[^\r\n]{5,80})"), false),
             new("period_end",
                 Re(@"(?:pay\s*period\s*(?:end|through)?|period\s*end)\s*[:#]?\s*(?<v>\d{1,2}[/-]\d{1,2}[/-]\d{2,4})"), true),
             new("gross_pay",
                 Re(@"gross\s*pay\s*[:#]?\s*\$?\s*(?<v>[\d,]+\.\d{2})"), false)],
            DateField: "period_end", FreshWithinDays: 90,
            ExpiryInFuture: false),

        new(DocKind.BankStatement,
            [Re(@"bank.{0,15}statement"), Re(@"statement")],
            [(Re(@"account summary"), 3), (Re(@"statement period"), 3),
             (Re(@"beginning balance"), 2), (Re(@"ending balance"), 2),
             (Re(@"routing\s*(?:number|no\.?|#)"), 1)],
            [new("statement_date",
                 Re(@"statement\s*date\s*[:#]?\s*(?<v>\d{1,2}[/-]\d{1,2}[/-]\d{2,4})"), true),
             new("ending_balance",
                 Re(@"(?:ending|closing)\s*balance\s*[:#]?\s*\$?\s*(?<v>[\d,]+\.\d{2})"), false),
             new("account_tail",
                 Re(@"account\s*(?:number|no\.?|#)\s*[:#]?\s*\**(?<v>\d{4})"), false)],
            DateField: "statement_date", FreshWithinDays: 120,
            ExpiryInFuture: false),

        new(DocKind.Id,
            [Re(@"passport"), Re(@"driver'?s?\s*licen[sc]e"),
             Re(@"\bdl[_-]"), Re(@"\bid[_-]")],
            [(Re(@"driver'?s?\s*licen[sc]e"), 3), (Re(@"passport"), 3),
             (Re(@"date of birth"), 1.5), (Re(@"expir\w*"), 1)],
            [new("expiry",
                 Re(@"expir\w*\s*(?:date)?\s*[:#]?\s*(?<v>\d{1,2}[/-]\d{1,2}[/-]\d{2,4})"), false),
             new("date_of_birth",
                 Re(@"date\s*of\s*birth\s*[:#]?\s*(?<v>\d{1,2}[/-]\d{1,2}[/-]\d{2,4})"), false)],
            DateField: "expiry", FreshWithinDays: 0,
            ExpiryInFuture: true),

        new(DocKind.FloodCert,
            [Re(@"flood")],
            [(Re(@"flood\s*(?:certification|cert)"), 3),
             (Re(@"flood\s*zone"), 2), (Re(@"\bnfip\b"), 1)],
            [new("flood_zone",
                 Re(@"flood\s*zone\s*[:#]?\s*(?<v>[A-Z0-9]{1,4})"), false),
             new("issue_date",
                 Re(@"issue\s*date\s*[:#]?\s*(?<v>\d{1,2}[/-]\d{1,2}[/-]\d{2,4})"), false)],
            DateField: "issue_date", FreshWithinDays: 365,
            ExpiryInFuture: false),
    ];

    public Task<DocClassification> ClassifyAsync(VaultDocRecord doc,
        ReadOnlyMemory<byte> head, CancellationToken ct = default)
    {
        var text = ExtractText(head);
        var findings = new List<DocFinding>();
        var fields = new Dictionary<string, string>();

        // Score each kind rule: filename bonus + weighted markers.
        KindRule? best = null;
        double bestScore = 0;
        foreach (var rule in Rules)
        {
            var score = rule.Filename.Any(f => f.IsMatch(doc.Filename))
                ? FilenameBonus : 0;
            foreach (var (pattern, weight) in rule.Markers)
                if (pattern.IsMatch(text)) score += weight;
            if (score > bestScore) { bestScore = score; best = rule; }
        }

        var detected = best?.Kind ?? DocKind.Misc;
        var confidence = bestScore > 0
            ? Math.Min(1.0, bestScore / 5.0) : 0.05;

        var declared = DocKind.Normalize(doc.Classification);
        if (detected == DocKind.Misc && text.Length < MinTextChars)
            findings.Add(new DocFinding(DocFindingKind.Unreadable,
                "no readable text or filename signal"));
        else if (declared is { Length: > 0 }
                 && !DocKind.Misc.Equals(declared)
                 && detected != DocKind.Misc
                 && !declared.Equals(detected,
                     StringComparison.OrdinalIgnoreCase))
            findings.Add(new DocFinding(DocFindingKind.KindMismatch,
                $"declared {declared}, detected {detected}"));

        if (best is not null)
        {
            foreach (var ex in best.Extractors)
            {
                var m = ex.Pattern.Match(text);
                if (m.Success)
                    fields[ex.Name] = m.Groups["v"].Value.Trim();
                else if (ex.Required)
                    findings.Add(new DocFinding(
                        DocFindingKind.MissingField, ex.Name));
            }
            CheckExpiry(best, fields, findings);
        }

        return Task.FromResult(new DocClassification(
            detected, confidence, fields, findings));
    }

    private void CheckExpiry(KindRule rule,
        IReadOnlyDictionary<string, string> fields,
        List<DocFinding> findings)
    {
        if (rule.DateField is null
            || !fields.TryGetValue(rule.DateField, out var raw)
            || !TryParseDate(raw, out var date))
            return;
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var expired = rule.ExpiryInFuture
            ? date < today
            : rule.FreshWithinDays > 0
                && date < today.AddDays(-rule.FreshWithinDays);
        if (expired)
            findings.Add(new DocFinding(DocFindingKind.ExpiredDoc,
                $"{rule.DateField} {raw}"));
    }

    /// Strings-style extraction — printable-ASCII runs ≥4 chars joined
    /// on newlines. Reads .txt and text-bearing PDFs; image scans
    /// yield nothing usable, which is what `unreadable` reports.
    private static string ExtractText(ReadOnlyMemory<byte> head)
    {
        var span = head.Span;
        var sb = new StringBuilder(
            Math.Min(span.Length, MaxTextChars));
        var run = 0;
        for (var i = 0; i < span.Length
            && sb.Length < MaxTextChars; i++)
        {
            var b = span[i];
            var printable = b is >= 0x20 and <= 0x7E || b == 0x09;
            if (printable) run++;
            else
            {
                if (run >= 4)
                {
                    for (var j = i - run; j < i; j++)
                        sb.Append((char)span[j]);
                    sb.Append('\n');
                }
                run = 0;
            }
        }
        if (run >= 4)
            for (var j = span.Length - run; j < span.Length; j++)
                sb.Append((char)span[j]);
        return sb.ToString();
    }

    private static bool TryParseDate(string raw, out DateOnly date)
    {
        var t = raw.Trim();
        if (DateTime.TryParse(t,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
        {
            date = DateOnly.FromDateTime(dt);
            return true;
        }
        date = default;
        return false;
    }
}
