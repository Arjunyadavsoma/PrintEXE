using PrintShopCompanion.Infrastructure.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Xunit;

namespace PrintShopCompanion.Infrastructure.Tests;

/// <summary>Decision D-06 — the token redactor must scrub credential-like properties.</summary>
public sealed class DeviceTokenRedactorTests
{
    private sealed class CollectingSink : ILogEventSink
    {
        public List<LogEvent> Events { get; } = new();
        public void Emit(LogEvent logEvent) => Events.Add(logEvent);
    }

    private static (CollectingSink Sink, ILogger Logger) Create()
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .Enrich.With(new DeviceTokenRedactor())
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (sink, logger);
    }

    [Theory]
    [InlineData("DeviceToken")]
    [InlineData("AccessToken")]
    [InlineData("RefreshToken")]
    [InlineData("Secret")]
    [InlineData("Password")]
    public void Sensitive_properties_are_replaced_with_mask(string propertyName)
    {
        var (sink, logger) = Create();
        // Enrichers run when the LOGGER writes (not when a sink receives an
        // event directly) — emit through the pipeline with a dynamic template
        // carrying the sensitive property name.
        logger.Information("x {" + propertyName + "}", "super-secret-value");
        var emitted = sink.Events.Single();
        Assert.Equal("***", ((ScalarValue)emitted.Properties[propertyName]).Value);
    }

    [Fact]
    public void Ordinary_properties_pass_through()
    {
        var (sink, logger) = Create();
        logger.Information("printing {JobId} on {PrinterName}", "J-1", "HP LaserJet");
        var evt = sink.Events.Single();
        Assert.Equal("J-1", ((ScalarValue)evt.Properties["JobId"]).Value);
        Assert.Equal("HP LaserJet", ((ScalarValue)evt.Properties["PrinterName"]).Value);
    }
}
