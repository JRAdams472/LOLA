using System.Globalization;

namespace Lola.Domain.Applications;

/// Reg B / ECOA retention (MPE-118, invariant 9): an application record
/// must be kept 25 months from its created_utc. Tenant erasure — which
/// crypto-shreds the whole file — is refused while any application is
/// still inside its window; an explicit operator override is the only
/// way through, and it lands in the control-plane audit log.
public static class RetentionPolicy
{
    public const int Months = 25;

    /// created_utc + 25 months. Input is the tenant file's ISO-8601 Z
    /// text (or any parseable UTC stamp); output is the boundary —
    /// retention is satisfied strictly after this instant.
    public static DateTimeOffset Until(string createdUtc) =>
        DateTimeOffset.Parse(createdUtc, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal)
            .AddMonths(Months);
}
