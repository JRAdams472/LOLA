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

    [Fact]
    public void Vault_capability_signature_is_masked()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .Enrich.WithSensitiveDataMasking(o => o.MaskingOperators = Logging.MaskingOperators)
            .WriteTo.Sink(sink)
            .CreateLogger();

        // Framework request logging captures the QueryString property —
        // a live sig is a credential and must not persist into sinks.
        logger.Information("Request starting GET {Path}{QueryString}",
            "/api/vault-content/vlt-1",
            "?tid=t1&exp=1700000900&sig=deadbeefcafe1234567890");

        sink.Rendered.ShouldNotContain("deadbeefcafe1234567890");
        sink.Rendered.ShouldContain("sig=");
    }

    private sealed class CollectingSink : ILogEventSink
    {
        public string Rendered { get; private set; } = "";
        public void Emit(LogEvent logEvent) => Rendered += logEvent.RenderMessage() + "\n";
    }
}
