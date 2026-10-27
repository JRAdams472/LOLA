using System.Text.RegularExpressions;
using Serilog.Enrichers.Sensitive;

namespace Lola.Api.Observability;

/// PII masking operators applied to every log event before ANY sink
/// sees it (console included — invariant 3: no PII in logs). This is
/// defense-in-depth regex scrubbing; the classification-driven field
/// policy lands with the domain model in M5+.
public static class Logging
{
    public static List<IMaskingOperator> MaskingOperators =>
    [
        new PatternMaskingOperator(@"\b\d{3}-\d{2}-\d{4}\b"), // SSN 123-45-6789
        new PatternMaskingOperator(@"\b\d{9}\b"), // SSN 123456789 — may catch 9-digit ids; masked-by-default wins
        new EmailAddressMaskingOperator()
    ];
}

/// Minimal regex masking operator — the package's RegexMaskingOperator
/// base is abstract, so SSN-style patterns get a one-method impl.
public sealed class PatternMaskingOperator(string pattern) : IMaskingOperator
{
    private readonly Regex _regex = new(pattern, RegexOptions.Compiled);

    public MaskingResult Mask(string input, string mask)
    {
        var result = _regex.Replace(input, mask);
        return new MaskingResult { Match = result != input, Result = result };
    }
}
