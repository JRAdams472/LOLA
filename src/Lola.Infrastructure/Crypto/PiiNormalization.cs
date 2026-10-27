using Lola.Domain.Classification;

namespace Lola.Infrastructure.Crypto;

/// Canonicalization before blind-index HMAC. Equality search only works
/// if "123-45-6789" and "123456789" index identically — formatting noise
/// must die here, deterministically.
public static class PiiNormalization
{
    public static string Normalize(PiiClass classification, string value) => classification switch
    {
        // Numbers-only classes: strip every non-digit.
        PiiClass.Ssn or PiiClass.Account => DigitsOnly(value),
        // Contact is email OR phone: emails lowercase+trim, anything else
        // digits (dashes/parens/spaces in phone numbers are noise).
        PiiClass.Contact => value.Contains('@') ? TextNorm(value) : DigitsOnly(value),
        _ => TextNorm(value)
    };

    private static string DigitsOnly(string value) =>
        new(value.Where(char.IsDigit).ToArray());

    private static string TextNorm(string value) =>
        value.Trim().ToLowerInvariant();
}
