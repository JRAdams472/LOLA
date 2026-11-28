namespace Lola.Domain.Credit;

/// AVM domain model (M26) — automated valuation model results.
/// Vendor-neutral: adapters parse vendor wire formats into this shape.
/// `Confidence` is the vendor-reported confidence/score normalized to
/// 0-100 when the vendor publishes on another scale; `Fsd` (forecast
/// standard deviation) is preserved raw when reported because pricing
/// engines consume it directly.
public static class AvmModel
{
    public sealed record Result(
        string Model,
        decimal Value,
        decimal? Confidence,
        decimal? Fsd,
        decimal? ValueHigh,
        decimal? ValueLow,
        string ValuationDate,
        string Vendor);

    /// Confidence normalization — vendors report on different scales:
    /// most publish 0-100 or 0-1. Normalize to 0-100; out-of-range or
    /// missing input yields null rather than an invented number.
    public static decimal? NormalizeConfidence(decimal? raw)
    {
        if (raw is null) return null;
        var v = raw.Value;
        if (v is >= 0m and <= 1m) return v * 100m;
        if (v is > 1m and <= 100m) return v;
        return null;
    }
}
