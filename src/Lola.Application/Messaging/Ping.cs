namespace Lola.Application.Messaging;

public sealed record PingQuery;

public static class PingHandler
{
    public static string Handle(PingQuery query) => "pong";
}
