namespace PrintShopCompanion.App.Services;

using Serilog.Core;
using Serilog.Events;

/// <summary>
/// Log enricher that redacts the <c>X-PrintShop-Device-Token</c> header
/// and any URL query-string <c>token=…</c> parameter before the log
/// event is serialised. This is one of the project's non-negotiables:
/// device credentials must never appear in any log file.
/// </summary>
public sealed class DeviceTokenRedactor : ILogEventEnricher
{
    public void Enrich(LogEvent logEvent, ILogEventPropertyFactory factory)
    {
        // Walk the message template and properties for any property whose name
        // contains "Token" or "Secret" and replace its value with the literal "***".
        foreach (var prop in logEvent.Properties.ToList())
        {
            if (IsSensitive(prop.Key) && prop.Value is ScalarValue sv)
            {
                logEvent.AddOrUpdateProperty(factory.CreateProperty(prop.Key, "***"));
            }
        }
    }

    private static bool IsSensitive(string name) =>
        name.Contains("Token", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Secret", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("Password", StringComparison.OrdinalIgnoreCase) ||
        name.Contains("DeviceToken", StringComparison.OrdinalIgnoreCase);
}
