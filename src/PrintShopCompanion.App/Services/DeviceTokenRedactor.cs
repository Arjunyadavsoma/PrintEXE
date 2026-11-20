namespace PrintShopCompanion.App.Services;

using System.Text.RegularExpressions;
using Serilog.Core;
using Serilog.Events;
using Serilog.Parsing;

/// <summary>
/// Redacts device credentials from every log event — one of the project's
/// non-negotiables: device tokens must never appear in any log file.
///
/// Phase 4 (B-012) hardening. The original enricher only replaced property
/// VALUES whose property NAME looked sensitive, and was never even wired
/// into the logger. Two gaps:
///  1. Tokens interpolated into the message template TEXT (literal strings,
///     e.g. <c>Log.Information("auth {H}", "Bearer xyz")</c> destructured into
///     the template) escaped the name-based pass;
///  2. nothing scrubbed the rendered message.
///
/// Now: the class is BOTH an enricher (scrubs sensitive property values,
/// name-based plus inline header/bearer/token patterns inside those values)
/// AND an <see cref="ILogEventSink"/> decorator that rewrites the message
/// template text when it matches token-shaped patterns, then forwards the
/// cleaned event to the inner sink. The App wires the File sink through it:
/// <c>.WriteTo.Sink(new DeviceTokenRedactor(fileSink))</c>.
/// </summary>
public sealed class DeviceTokenRedactor : ILogEventEnricher, ILogEventSink
{
    private static readonly Regex InlineSensitive = new(
        @"(?i)((x-printshop-device-token|authorization)\s*[:=]\s*)(\S+)|((bearer)\s+)(\S+)|(([\?&])token=)([^&\s""']+)",
        RegexOptions.Compiled);

    private static readonly MessageTemplateParser TemplateParser = new();

    private readonly ILogEventSink? _inner;

    public DeviceTokenRedactor(ILogEventSink? inner = null) => _inner = inner;

    /// <summary>Sink path: scrub properties + template text, forward to the inner sink.</summary>
    public void Emit(LogEvent logEvent)
    {
        Enrich(logEvent, SimplePropertyFactory.Instance);

        var text = logEvent.MessageTemplate.Text;
        if (text is not null && InlineSensitive.IsMatch(text))
        {
            var cleaned = InlineSensitive.Replace(text, ScrubMatch);
            logEvent = new LogEvent(
                logEvent.Timestamp, logEvent.Level, logEvent.Exception,
                TemplateParser.Parse(cleaned),
                logEvent.Properties.OfType<LogEventProperty>());
        }

        _inner?.Emit(logEvent);
    }

    /// <summary>Enricher path: scrub sensitive property values in place.</summary>
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        foreach (var prop in logEvent.Properties.ToList())
        {
            if (!IsSensitive(prop.Key)) continue;
            if (prop.Value is ScalarValue sv && sv.Value is string s && !string.IsNullOrEmpty(s))
            {
                var scrubbed = InlineSensitive.Replace(s, ScrubMatch);
                logEvent.AddOrUpdateProperty(factory.CreateProperty(prop.Key, scrubbed));
            }
            else if (prop.Value is ScalarValue)
            {
                logEvent.AddOrUpdateProperty(factory.CreateProperty(prop.Key, "***"));
            }
        }
    }

    private static string ScrubMatch(Match m) =>
        m.Groups[1].Success ? m.Groups[1].Value + "***"
        : m.Groups[4].Success ? m.Groups[4].Value + "***"
        : m.Groups[7].Success ? m.Groups[7].Value + "***"
        : "***";

    private static bool IsSensitive(string name) =>
        name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("DeviceToken", StringComparison.OrdinalIgnoreCase);

    /// <summary>Minimal factory so the sink path can enrich without a Serilog-provided one.</summary>
    private sealed class SimplePropertyFactory : ILogEventPropertyFactory
    {
        public static readonly SimplePropertyFactory Instance = new();
        public LogEventProperty CreateProperty(string name, object? value, bool destructureObjects = false) =>
            new(name, value is null ? new ScalarValue(string.Empty) : new ScalarValue(value));
    }
}
