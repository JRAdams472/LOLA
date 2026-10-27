using Lola.Domain.Classification;
using Shouldly;

namespace Lola.Domain.Tests;

public class PiiClassTests
{
    [Fact]
    public void Attribute_carries_classification()
    {
        var attr = new PiiClassAttribute(PiiClass.Ssn);
        attr.Class.ShouldBe(PiiClass.Ssn);
    }

    [Fact]
    public void Searchable_flag_defaults_false_and_sets()
    {
        new PiiClassAttribute(PiiClass.Ssn).Searchable.ShouldBeFalse();
        new PiiClassAttribute(PiiClass.Ssn) { Searchable = true }.Searchable.ShouldBeTrue();
    }
}
