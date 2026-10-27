using Lola.Api.Observability;
using Serilog;
using Serilog.Core;
using Serilog.Enrichers.Sensitive;
using Serilog.Events;
using Shouldly;

namespace Lola.Api.Tests;

public class PiiRedactionTests
{
    [Fact]
    public void Pii_is_masked_before_any_sink()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .Enrich.WithSensitiveDataMasking(o => o.MaskingOperators = Logging.MaskingOperators)
            .WriteTo.Sink(sink)
            .CreateLogger();

        logger.Information("borrower ssn {Ssn} contact {Contact}", "123-45-6789", "borrower@example.com");
        logger.Information("literal ssn 123-45-6789 in template text");

        sink.Rendered.ShouldNotContain("123-45-6789");
        sink.Rendered.ShouldNotContain("borrower@example.com");
        sink.Rendered.ShouldContain("MASK");
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public string Rendered { get; private set; } = "";
        public void Emit(LogEvent logEvent) => Rendered += logEvent.RenderMessage() + "\n";
    }
}
