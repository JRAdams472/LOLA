using Shouldly;

namespace Lola.Domain.Tests;

public class ArchitectureTests
{
    private static readonly string[] Forbidden =
        ["Lola.Infrastructure", "Lola.ControlPlane", "Lola.Api"];

    [Fact]
    public void Domain_has_no_upper_layer_dependencies()
    {
        var refs = typeof(Lola.Domain.Classification.PiiClassAttribute)
            .Assembly.GetReferencedAssemblies().Select(a => a.Name);
        refs.ShouldNotContain(r => Forbidden.Any(f => r == f));
    }

    [Fact]
    public void Application_has_no_upper_layer_dependencies()
    {
        var refs = typeof(Lola.Application.Messaging.PingQuery)
            .Assembly.GetReferencedAssemblies().Select(a => a.Name);
        refs.ShouldNotContain(r => Forbidden.Any(f => r == f));
    }
}
