using Microsoft.Extensions.Logging;
using Wolverine;

namespace Lola.Application.Behaviors;

/// Wolverine middleware — logs the message type being handled. Keeps
/// message execution visible in traces/logs without business noise.
/// Never log message payloads (invariant 3: no PII in logs).
public class LogExecution
{
    public void Before(Envelope envelope, ILogger<LogExecution> logger)
        => logger.LogDebug("Handling {MessageType}", envelope.Message?.GetType().Name);
}
