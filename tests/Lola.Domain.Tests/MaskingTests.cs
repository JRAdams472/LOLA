using Lola.Application.Identity;
using Lola.Application.Security;
using Lola.Domain.Classification;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Shouldly;
using Wolverine;

namespace Lola.Domain.Tests;

/// MPE-86: proves the masked-invoke seam actually masks handler
/// results in place (and that [PiiClass] traversal works standalone).
public sealed class MaskingTests
{
    public sealed record ProbeQuery;
    public sealed record ProbeDto(
        string Id,
        [property: PiiClass(PiiClass.Contact)] string Email,
        [property: PiiClass(PiiClass.Account)] string? SecretRef);

    public static class ProbeHandler
    {
        public static ProbeDto Handle(ProbeQuery _)
            => new("p1", "user@test.dev", "vault:secret-1234");
    }

    /// Regression guard for the design note in MaskedInvoke: Wolverine
    /// After-methods resolve unknown parameters as services/messages —
    /// `object? result` binds a plain System.Object, never the handler's
    /// return value. If a future Wolverine version binds results here,
    /// masking could move back into middleware.
    public class RecordingBehavior
    {
        public static object? Seen;
        public void After(Envelope envelope, object? result) => Seen = result;
    }

    [Fact]
    public async Task Wolverine_after_cannot_observe_handler_results()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType(typeof(ProbeHandler));
                opts.Policies.AddMiddleware<RecordingBehavior>();
            })
            .StartAsync();

        var bus = host.Services.GetRequiredService<IMessageBus>();
        await bus.InvokeAsync<ProbeDto>(new ProbeQuery());
        RecordingBehavior.Seen.ShouldNotBeOfType<ProbeDto>();
    }

    [Fact]
    public async Task InvokeMaskedAsync_masks_pii_members_on_handler_result()
    {
        using var host = await Host.CreateDefaultBuilder()
            .UseWolverine(opts =>
            {
                opts.Discovery.DisableConventionalDiscovery();
                opts.Discovery.IncludeType(typeof(ProbeHandler));
            })
            .StartAsync();

        using (VisibilityContext.Use(new HashSet<PiiClass>())) // nothing visible
        {
            var bus = host.Services.GetRequiredService<IMessageBus>();
            var dto = await bus.InvokeMaskedAsync<ProbeDto>(new ProbeQuery());
            dto.Email.ShouldStartWith("•••");
            dto.SecretRef.ShouldStartWith("•••");
        }
    }

    [Fact]
    public void Masker_respects_visibility_and_masks_nested_dto()
    {
        var dto = new ProbeDto("p1", "user@test.dev", "vault:secret-1234");
        var list = new List<ProbeDto> { dto };
        using (VisibilityContext.Use(new HashSet<PiiClass> { PiiClass.Contact }))
        {
            PiiMasker.MaskInPlace(list);
        }
        dto.Email.ShouldBe("user@test.dev");          // Contact visible
        dto.SecretRef.ShouldStartWith("•••");          // Account masked
        dto.SecretRef.ShouldEndWith("1234");           // last-4 survives
    }
}
