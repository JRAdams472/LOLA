import { businessDaysUntil, isFederalHoliday, isBusinessDay } from "@/lib/trid";

/// Parity with TridClock.cs — same Reg Z general business-day rule.

describe("isFederalHoliday", () => {
  it.each([
    ["2026-01-01", true], // New Year's Day
    ["2026-07-04", true], // Independence Day
    ["2026-12-25", true], // Christmas
    ["2026-06-19", true], // Juneteenth
    ["2026-11-11", true], // Veterans Day
    ["2026-01-19", true], // MLK Day (3rd Mon Jan 2026)
    ["2026-05-25", true], // Memorial Day (last Mon May 2026)
    ["2026-09-07", true], // Labor Day (1st Mon Sep 2026)
    ["2026-11-26", true], // Thanksgiving (4th Thu Nov 2026)
    ["2026-01-12", false],
    ["2026-07-03", false], // observed-adjacent Friday is NOT the holiday
  ])("%s → %s", (iso, expected) => {
    expect(isFederalHoliday(new Date(`${iso}T12:00:00Z`))).toBe(expected);
  });
});

describe("isBusinessDay", () => {
  it("counts Saturdays, skips Sundays and holidays", () => {
    expect(isBusinessDay(new Date("2026-01-10T12:00:00Z"))).toBe(true); // Sat
    expect(isBusinessDay(new Date("2026-01-11T12:00:00Z"))).toBe(false); // Sun
    expect(isBusinessDay(new Date("2026-12-25T12:00:00Z"))).toBe(false);
  });
});

describe("businessDaysUntil", () => {
  // Backend case: stamp Thu 2026-01-08 → deadline Mon 2026-01-12.
  const deadline = "2026-01-12T00:00:00.000Z";

  it("counts business days in (today, deadline]", () => {
    expect(businessDaysUntil(deadline, new Date("2026-01-08T10:00:00Z"))).toBe(3);
    expect(businessDaysUntil(deadline, new Date("2026-01-10T10:00:00Z"))).toBe(1); // Sat → Mon
    expect(businessDaysUntil(deadline, new Date("2026-01-11T10:00:00Z"))).toBe(1); // Sun → Mon
  });

  it("returns 0 on the deadline day", () => {
    expect(businessDaysUntil(deadline, new Date("2026-01-12T09:00:00Z"))).toBe(0);
  });

  it("goes negative by calendar days once overdue", () => {
    expect(businessDaysUntil(deadline, new Date("2026-01-14T09:00:00Z"))).toBe(-2);
  });
});
