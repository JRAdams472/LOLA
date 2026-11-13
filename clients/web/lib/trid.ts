/// TRID countdown math — mirrors TridClock.cs: the Reg Z general
/// business-day rule (12 CFR 1026.2(a)(6)) counts every calendar day
/// except Sundays and the federal legal public holidays (actual
/// dates, not observed-adjacent); Saturdays DO count.

const DAY_MS = 86_400_000;

function nthWeekday(year: number, month: number, dow: number, n: number): Date {
  const d = new Date(Date.UTC(year, month - 1, 1));
  while (d.getUTCDay() !== dow) d.setUTCDate(d.getUTCDate() + 1);
  d.setUTCDate(d.getUTCDate() + 7 * (n - 1));
  return d;
}

function lastWeekday(year: number, month: number, dow: number): Date {
  const d = new Date(Date.UTC(year, month, 0));
  while (d.getUTCDay() !== dow) d.setUTCDate(d.getUTCDate() - 1);
  return d;
}

const sameDay = (a: Date, b: Date) =>
  a.getUTCFullYear() === b.getUTCFullYear() &&
  a.getUTCMonth() === b.getUTCMonth() &&
  a.getUTCDate() === b.getUTCDate();

export function isFederalHoliday(d: Date): boolean {
  const m = d.getUTCMonth() + 1;
  const day = d.getUTCDate();
  const y = d.getUTCFullYear();
  if (m === 1 && day === 1) return true; // New Year's Day
  if (m === 6 && day === 19) return true; // Juneteenth
  if (m === 7 && day === 4) return true; // Independence Day
  if (m === 11 && day === 11) return true; // Veterans Day
  if (m === 12 && day === 25) return true; // Christmas
  if (sameDay(d, nthWeekday(y, 1, 1, 3))) return true; // MLK Day
  if (sameDay(d, nthWeekday(y, 2, 1, 3))) return true; // Washington's Birthday
  if (sameDay(d, lastWeekday(y, 5, 1))) return true; // Memorial Day
  if (sameDay(d, nthWeekday(y, 9, 1, 1))) return true; // Labor Day
  if (sameDay(d, nthWeekday(y, 10, 1, 2))) return true; // Columbus Day
  return sameDay(d, nthWeekday(y, 11, 4, 4)); // Thanksgiving
}

export function isBusinessDay(d: Date): boolean {
  return d.getUTCDay() !== 0 && !isFederalHoliday(d);
}

const startOfUtcDay = (d: Date) =>
  new Date(Date.UTC(d.getUTCFullYear(), d.getUTCMonth(), d.getUTCDate()));

/** Business days left until the TRID deadline, counting business days
 *  in (today, deadline] — 0 means due today; negative means the
 *  deadline passed that many calendar days ago. */
export function businessDaysUntil(
  deadlineUtc: string,
  from: Date = new Date(),
): number {
  const deadline = startOfUtcDay(new Date(deadlineUtc));
  const today = startOfUtcDay(from);
  if (deadline.getTime() < today.getTime()) {
    return Math.round((deadline.getTime() - today.getTime()) / DAY_MS);
  }
  let left = 0;
  const cursor = new Date(today);
  while (cursor.getTime() < deadline.getTime()) {
    cursor.setUTCDate(cursor.getUTCDate() + 1);
    if (isBusinessDay(cursor)) left++;
  }
  return left;
}
