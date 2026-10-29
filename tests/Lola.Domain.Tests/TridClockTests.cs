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
    public void Deadline_skips_weekends()
    {
        // Thursday stamp → Fri(1), Mon(2), Tue(3) → Tuesday deadline.
        TridClock.DeadlineUtc("2026-10-29T09:15:00.000Z")
            .ShouldBe("2026-11-03T00:00:00.000Z");
        // Friday stamp → Mon(1), Tue(2), Wed(3) → Wednesday.
        TridClock.DeadlineUtc("2026-10-30T09:15:00.000Z")
            .ShouldBe("2026-11-04T00:00:00.000Z");
    }

    [Fact]
    public void Deadline_null_when_clock_not_started()
        => TridClock.DeadlineUtc(null).ShouldBeNull();
}
