using Lola.Domain.Applications;
using Shouldly;
using Xunit;

namespace Lola.Domain.Tests;

public sealed class TridClockTests
{
    private static ApplicationDocument DocWith(params (string Path, string Value)[] fields)
    {
        var doc = ApplicationDocument.Empty();
        foreach (var (path, value) in fields) doc.Set(path, value);
        return doc;
    }

    [Fact]
    public void Complete_false_until_all_six_triggers_present()
    {
        // Five of the six catalog `trid` fields — still incomplete.
        var doc = DocWith(
            ("loan_amount", "450000"),
            ("party[0].first_name", "Ada"),
            ("party[0].ssn", "123456789"),
            ("party[0].employment[0].monthly_income", "8000"),
            ("subject_property.street", "1 Main St"));
        TridClock.Complete(doc).ShouldBeFalse();
    }

    [Fact]
    public void Complete_true_when_all_six_triggers_present()
    {
        var doc = DocWith(
            ("loan_amount", "450000"),
            ("party[0].first_name", "Ada"),
            ("party[0].ssn", "123456789"),
            ("party[0].employment[0].monthly_income", "8000"),
            ("subject_property.street", "1 Main St"),
            ("subject_property.est_value", "620000"));
        TridClock.Complete(doc).ShouldBeTrue();
    }

    [Fact]
    public void Deadline_counts_saturdays_skips_sundays()
    {
        // Reg Z general business-day rule: Saturdays count.
        // Thu Oct 29 2026 → Fri(1), Sat(2), Sun(skip), Mon(3) → Nov 2.
        TridClock.DeadlineUtc("2026-10-29T09:15:00.000Z")
            .ShouldBe("2026-11-02T00:00:00.000Z");
        // Fri Oct 30 → Sat(1), Sun(skip), Mon(2), Tue(3) → Nov 3.
        TridClock.DeadlineUtc("2026-10-30T09:15:00.000Z")
            .ShouldBe("2026-11-03T00:00:00.000Z");
    }

    [Fact]
    public void Deadline_skips_federal_holidays()
    {
        // Thu Dec 24 2026 → Fri Dec 25 = Christmas (skip), Sat(1),
        // Sun(skip), Mon(2), Tue(3) → Dec 29.
        TridClock.DeadlineUtc("2026-12-24T09:15:00.000Z")
            .ShouldBe("2026-12-29T00:00:00.000Z");
    }

    [Fact]
    public void Federal_holiday_calendar_covers_all_eleven()
    {
        FederalHolidays.Is(new DateTime(2026, 1, 1)).ShouldBeTrue();    // New Year
        FederalHolidays.Is(new DateTime(2026, 1, 19)).ShouldBeTrue();   // MLK (3rd Mon Jan)
        FederalHolidays.Is(new DateTime(2026, 2, 16)).ShouldBeTrue();   // Washington (3rd Mon Feb)
        FederalHolidays.Is(new DateTime(2026, 5, 25)).ShouldBeTrue();   // Memorial (last Mon May)
        FederalHolidays.Is(new DateTime(2026, 6, 19)).ShouldBeTrue();   // Juneteenth
        FederalHolidays.Is(new DateTime(2026, 7, 4)).ShouldBeTrue();    // Independence
        FederalHolidays.Is(new DateTime(2026, 9, 7)).ShouldBeTrue();    // Labor (1st Mon Sep)
        FederalHolidays.Is(new DateTime(2026, 10, 12)).ShouldBeTrue();  // Columbus (2nd Mon Oct)
        FederalHolidays.Is(new DateTime(2026, 11, 11)).ShouldBeTrue();  // Veterans
        FederalHolidays.Is(new DateTime(2026, 11, 26)).ShouldBeTrue();  // Thanksgiving (4th Thu Nov)
        FederalHolidays.Is(new DateTime(2026, 12, 25)).ShouldBeTrue();  // Christmas
        FederalHolidays.Is(new DateTime(2026, 12, 24)).ShouldBeFalse();
        FederalHolidays.Is(new DateTime(2026, 7, 3)).ShouldBeFalse();   // observed ≠ holiday date
    }

    [Fact]
    public void Deadline_null_when_clock_not_started()
        => TridClock.DeadlineUtc(null).ShouldBeNull();
}
