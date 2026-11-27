namespace Lola.Domain.Hmda;

/// Universal Loan Identifier per 12 CFR 1003.4(a)(1)(i) + Appendix C:
/// LEI (20 chars) + up to 23 loan-unique additional characters + a
/// two-digit ISO/IEC 7064 MOD 97-10 check digit. Additional characters
/// must be alphanumeric and must not identify the borrower.
public static class Uli
{
    /// Generates the full ULI for a base string (LEI + additional
    /// characters). Throws on invalid characters or length — callers
    /// pass institution LEI + application-derived suffix.
    public static string Generate(string lei, string additionalCharacters)
    {
        if (string.IsNullOrWhiteSpace(lei) || lei.Length != 20)
            throw new ArgumentException("LEI must be exactly 20 characters", nameof(lei));
        if (additionalCharacters.Length is < 1 or > 23)
            throw new ArgumentException("additional characters must be 1-23 chars", nameof(additionalCharacters));
        var baseStr = (lei + additionalCharacters).ToUpperInvariant();
        foreach (var ch in baseStr)
        {
            if (!char.IsDigit(ch) && ch is < 'A' or > 'Z')
                throw new ArgumentException($"invalid ULI character '{ch}' — alphanumeric only", nameof(additionalCharacters));
        }
        return baseStr + CheckDigit(baseStr);
    }

    /// ISO/IEC 7064 MOD 97-10 over the alphanumeric string with "00"
    /// appended, per Appendix C steps 1-5.
    public static string CheckDigit(string baseString)
    {
        var numeric = ToNumeric(baseString) + "00";
        var check = 98 - Mod97(numeric);
        return check.ToString("D2");
    }

    /// Validation per Appendix C: converting the full ULI to numeric
    /// and applying mod 97 must yield 1.
    public static bool Validate(string uli) =>
        !string.IsNullOrWhiteSpace(uli) && Mod97(ToNumeric(uli.ToUpperInvariant())) == 1;

    private static string ToNumeric(string s)
    {
        var sb = new System.Text.StringBuilder(s.Length * 2);
        foreach (var ch in s)
        {
            if (char.IsDigit(ch)) sb.Append(ch);
            else if (ch is >= 'A' and <= 'Z') sb.Append(ch - 55); // A=10..Z=35
            else throw new ArgumentException($"invalid ULI character '{ch}'");
        }
        return sb.ToString();
    }

    // Big-number mod without bignum math: process digit-by-digit.
    private static int Mod97(string digits)
    {
        var rem = 0;
        foreach (var d in digits)
            rem = (rem * 10 + (d - '0')) % 97;
        return rem;
    }
}
