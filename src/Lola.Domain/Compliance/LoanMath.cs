namespace Lola.Domain.Compliance;

/// Loan arithmetic behind the ATR/QM + high-cost battery (M22 P1,
/// MPE-306). Two pieces: the proposed monthly payment (DTI numerator
/// input) and the Reg Z Appendix J actuarial APR — the contract payment
/// stream is fixed at the note rate; the APR is the yield that makes the
/// stream's present value equal the amount financed (principal −
/// prepaid finance charges).
public static class LoanMath
{
    /// Level-payment monthly P&I for a fully amortizing fixed rate.
    public static decimal Payment(decimal principal, decimal annualRatePct,
        int months)
    {
        if (principal <= 0 || months <= 0) return 0m;
        var i = (double)(annualRatePct / 1200m);
        if (i <= 0) return Math.Round(principal / months, 2);
        var f = Math.Pow(1 + i, months);
        return Math.Round((decimal)((double)principal * i * f / (f - 1)), 2);
    }

    /// Interest-only monthly payment.
    public static decimal InterestOnlyPayment(decimal principal,
        decimal annualRatePct) => principal <= 0 || annualRatePct <= 0
        ? 0m : Math.Round(principal * annualRatePct / 1200m, 2);

    /// The payment the engine treats as the proposed obligation for
    /// DTI — interest-only at the note rate for `interest_only`,
    /// amortizing otherwise (`graduated` uses the fully-amortizing
    /// payment — the conservative figure).
    public static decimal ProposedPayment(decimal principal,
        decimal annualRatePct, int months, string? amortizationType) =>
        amortizationType == "interest_only"
            ? InterestOnlyPayment(principal, annualRatePct)
            : Payment(principal, annualRatePct, months);

    /// Appendix J APR. Solves the annual rate whose discounting of the
    /// contract payment stream equals `principal - prepaidFinance`.
    /// Bisection to ~0.0001pp; null when the loan is degenerate or the
    /// fees consume the principal entirely.
    public static decimal? Apr(decimal principal, decimal prepaidFinance,
        decimal annualRatePct, int months, string? amortizationType)
    {
        if (principal <= 0 || months <= 0 || annualRatePct < 0)
            return null;
        var financed = (double)(principal - prepaidFinance);
        if (financed <= 0) return null;

        // Payment stream fixed at the contract (note) rate.
        var iNote = (double)annualRatePct / 1200;
        double[] stream;
        if (amortizationType == "interest_only")
        {
            stream = new double[months];
            var io = (double)principal * iNote;
            for (var t = 0; t < months; t++) stream[t] = io;
            stream[months - 1] += (double)principal; // balloon of principal
        }
        else
        {
            var f = iNote <= 0 ? 1 : Math.Pow(1 + iNote, months);
            var p = iNote <= 0 ? (double)principal / months
                : (double)principal * iNote * f / (f - 1);
            stream = new double[months];
            Array.Fill(stream, p);
        }

        double Pv(double annual)
        {
            var i = annual / 12;
            double pv = 0;
            for (var t = 0; t < stream.Length; t++)
                pv += stream[t] / Math.Pow(1 + i, t + 1);
            return pv;
        }

        // Bracket the yield: at `hi` the discounted stream must price
        // BELOW the amount financed (higher rate → lower PV).
        double lo = 0, hi = Math.Max((double)annualRatePct / 100 * 2 + .05, .25);
        for (var n = 0; n < 60 && Pv(hi) > financed; n++) hi *= 2;
        if (Pv(hi) > financed) return null;
        for (var n = 0; n < 200; n++)
        {
            var mid = (lo + hi) / 2;
            if (Pv(mid) > financed) lo = mid; else hi = mid;
            if (hi - lo < 1e-9) break;
        }
        return Math.Round((decimal)((lo + hi) / 2 * 100), 4);
    }
}
