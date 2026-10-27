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
}
