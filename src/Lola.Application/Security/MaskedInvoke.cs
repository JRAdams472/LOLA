using Wolverine;

namespace Lola.Application.Security;

/// The single masking seam (ADR-0008/0011): every adapter — REST
/// endpoints and GraphQL resolvers — invokes handlers through this
/// extension, which masks [PiiClass] members on the result per the
/// current VisibilityContext. Wolverine's After-methods cannot see a
/// handler's return value (parameters resolve to services/messages,
/// never the result), so masking must wrap the invoke, not the chain.
public static class MaskedInvoke
{
    public static async Task<T> InvokeMaskedAsync<T>(
        this IMessageBus bus, object message, CancellationToken ct = default)
    {
        var result = await bus.InvokeAsync<T>(message, ct);
        PiiMasker.MaskInPlace(result);
        return result;
    }
}
