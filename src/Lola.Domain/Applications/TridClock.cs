using System.Text.Json.Nodes;

namespace Lola.Domain.Applications;

/// TRID (TILA-RESPA Integrated Disclosure) clock — the catalog's six
/// `trid` fields are the regulated trigger set (name, income, SSN,
/// property address, estimated value, loan amount). First co-presence
/// of all six stamps `trid_clock_start` once; the Loan Estimate must
/// be delivered within 3 business days of that stamp.
public static class TridClock
{
    private static readonly FieldCatalog Catalog = FieldCatalog.Current;

    /// All catalog `trid`-flagged paths hold a non-empty value?
    public static bool Complete(ApplicationDocument doc)
    {
        foreach (var spec in Catalog.TridFields)
        {
            var node = doc.Get(spec.Path);
            if (node is null) return false;
            var text = node is JsonValue v && v.TryGetValue<string>(out var s)
                ? s : node.ToJsonString();
            if (string.IsNullOrWhiteSpace(text)) return false;
        }
        return true;
    }

    /// Three business days after the stamp — Saturdays and Sundays do
    /// not count; the stamp day itself is day zero.
    public static string? DeadlineUtc(string? tridClockStart)
    {
        if (tridClockStart is null
            || !DateTimeOffset.TryParse(tridClockStart, out var start))
            return null;
        var day = start.Date;
        var remaining = 3;
        while (remaining > 0)
        {
            day = day.AddDays(1);
            if (day.DayOfWeek is not DayOfWeek.Saturday
                and not DayOfWeek.Sunday)
                remaining--;
        }
        return new DateTimeOffset(day, TimeSpan.Zero)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }
}
