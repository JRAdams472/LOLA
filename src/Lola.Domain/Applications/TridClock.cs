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

    /// Three business days after the stamp under the Reg Z GENERAL
    /// business-day rule (12 CFR 1026.2(a)(6)): every calendar day
    /// counts except Sundays and the federal legal public holidays —
    /// Saturdays DO count. The stamp day itself is day zero.
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
            if (IsBusinessDay(day)) remaining--;
        }
        return new DateTimeOffset(day, TimeSpan.Zero)
            .ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'");
    }

    private static bool IsBusinessDay(DateTime d) =>
        d.DayOfWeek is not DayOfWeek.Sunday && !FederalHolidays.Is(d);
}

/// Federal legal public holidays under 5 U.S.C. 6103 — actual calendar
/// dates (the Reg Z general rule uses the date itself, not the
/// observed-adjacent day). Fixed-date holidays are the literal date;
/// floating ones are nth/last weekday-of-month.
public static class FederalHolidays
{
    public static bool Is(DateTime d)
    {
        if (d.Month == 1 && d.Day == 1) return true;                    // New Year's Day
        if (d.Month == 6 && d.Day == 19) return true;                   // Juneteenth
        if (d.Month == 7 && d.Day == 4) return true;                    // Independence Day
        if (d.Month == 11 && d.Day == 11) return true;                  // Veterans Day
        if (d.Month == 12 && d.Day == 25) return true;                  // Christmas
        if (d == NthWeekday(d.Year, 1, DayOfWeek.Monday, 3)) return true;   // MLK Day
        if (d == NthWeekday(d.Year, 2, DayOfWeek.Monday, 3)) return true;   // Washington's Birthday
        if (d == LastWeekday(d.Year, 5, DayOfWeek.Monday)) return true;     // Memorial Day
        if (d == NthWeekday(d.Year, 9, DayOfWeek.Monday, 1)) return true;   // Labor Day
        if (d == NthWeekday(d.Year, 10, DayOfWeek.Monday, 2)) return true;  // Columbus Day
        return d == NthWeekday(d.Year, 11, DayOfWeek.Thursday, 4);          // Thanksgiving
    }

    private static DateTime NthWeekday(int year, int month, DayOfWeek dow, int n)
    {
        var d = new DateTime(year, month, 1);
        while (d.DayOfWeek != dow) d = d.AddDays(1);
        return d.AddDays(7 * (n - 1));
    }

    private static DateTime LastWeekday(int year, int month, DayOfWeek dow)
    {
        var d = new DateTime(year, month, DateTime.DaysInMonth(year, month));
        while (d.DayOfWeek != dow) d = d.AddDays(-1);
        return d;
    }
}
