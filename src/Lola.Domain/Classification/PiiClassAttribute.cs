namespace Lola.Domain.Classification;

public enum PiiClass
{
    Name,
    Address,
    Contact,
    Ssn,
    Dob,
    Account,
    Income,
    Other
}

/// Marks a member as containing personal data. Drives field-level
/// encryption (M2), response masking (M6), egress firewall (M7), and the
/// generated PII data-map. Placement is schema-enforced: a field outside
/// the net cannot silently hold PII.
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field, AllowMultiple = false)]
public sealed class PiiClassAttribute : Attribute
{
    public PiiClassAttribute(PiiClass classification) => Class = classification;

    public PiiClass Class { get; }
}
