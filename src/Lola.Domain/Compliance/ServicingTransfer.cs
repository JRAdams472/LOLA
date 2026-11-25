namespace Lola.Domain.Compliance;

/// Reg X §1024.33 — servicing-transfer notice deadlines. The
/// transferor must notify the borrower NOT LESS THAN 15 days before
/// the transfer's effective date; the transferee NOT MORE THAN 15
/// days after it. A notice is overdue when its send-by deadline has
/// passed with no sent timestamp.
public static class ServicingTransfer
{
    public const int TransferorDaysBeforeEffective = 15;
    public const int TransfereeDaysAfterEffective = 15;

    public enum Kind { Transferor, Transferee }

    /// The statutory send-by deadline for a notice kind given the
    /// transfer's effective date. Transferor: effective − 15 days
    /// (the LAST day sending is timely — sending ON this day means
    /// exactly 15 days of notice). Transferee: effective + 15 days.
    public static DateOnly SendBy(Kind kind, DateOnly effective) => kind switch
    {
        Kind.Transferor => effective.AddDays(-TransferorDaysBeforeEffective),
        Kind.Transferee => effective.AddDays(TransfereeDaysAfterEffective),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    /// Overdue when nothing was sent and `today` is strictly past the
    /// deadline — sending ON the deadline day is still timely.
    public static bool Overdue(DateOnly sendBy, DateOnly? sentOn, DateOnly today)
        => sentOn is null && today > sendBy;

    /// A notice sent late is a statutory violation regardless of the
    /// current date — tracked separately from pending-overdue.
    public static bool SentLate(DateOnly sendBy, DateOnly sentOn)
        => sentOn > sendBy;
}
