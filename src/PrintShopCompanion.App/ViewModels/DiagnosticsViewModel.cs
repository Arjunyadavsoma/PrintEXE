namespace PrintShopCompanion.App.ViewModels;

using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Infrastructure.Diagnostics;

/// <summary>
/// Diagnostics screen (decision D-07): one place that answers "is the agent
/// healthy?" — cloud connectivity, device registration, database health,
/// queue state, printer health, disk space, last successful print and recent
/// errors, plus the one-shot diagnostics export for support hand-off.
/// </summary>
public partial class DiagnosticsViewModel : ObservableObject
{
    private readonly IServiceProvider _services;

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private string _statusLine = "Run a diagnostic check to see the agent's health.";

    [ObservableProperty]
    private string _baseUrl = "";

    [ObservableProperty]
    private bool _cloudReachable;

    [ObservableProperty]
    private bool _tokenValid;

    [ObservableProperty]
    private bool _deviceRegistered;

    [ObservableProperty]
    private string _deviceId = "";

    [ObservableProperty]
    private string _shopId = "";

    [ObservableProperty]
    private bool _databaseHealthy;

    [ObservableProperty]
    private string _queueSummary = "";

    [ObservableProperty]
    private string _queueState = "";

    [ObservableProperty]
    private string _printerSummary = "";

    [ObservableProperty]
    private string _diskSummary = "";

    [ObservableProperty]
    private string _lastPrint = "never";

    [ObservableProperty]
    private string _errors = "";

    [ObservableProperty]
    private string _exportPath = "";

    public DiagnosticsViewModel(IServiceProvider services)
    {
        _services = services;
    }

    [RelayCommand]
    private async Task RunCheckAsync(CancellationToken ct)
    {
        IsBusy = true;
        Errors = "";
        try
        {
            var svc = _services.GetRequiredService<DiagnosticsService>();
            var snap = await svc.CollectAsync(ct);
            Apply(snap);
            StatusLine = snap.Healthy
                ? "All checks passed."
                : $"{snap.Errors.Count} issue(s) found — see the list below.";
        }
        catch (Exception ex)
        {
            StatusLine = "Diagnostic run failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    [RelayCommand]
    private async Task ExportAsync(CancellationToken ct)
    {
        IsBusy = true;
        try
        {
            var svc = _services.GetRequiredService<DiagnosticsService>();
            var path = Path.Combine(
                Path.GetTempPath(), "PrintShop",
                $"printexe-diagnostics-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.zip");
            ExportPath = await svc.ExportDiagnosticsAsync(path, ct);
            StatusLine = "Diagnostics bundle exported.";
        }
        catch (Exception ex)
        {
            StatusLine = "Export failed: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void Apply(DiagnosticsSnapshot s)
    {
        BaseUrl = s.BaseUrl ?? "";
        CloudReachable = s.CloudReachable;
        TokenValid = s.TokenValid;
        DeviceRegistered = s.DeviceRegistered;
        DeviceId = s.DeviceId ?? "";
        ShopId = s.ShopId ?? "";
        DatabaseHealthy = s.DatabaseHealthy;
        QueueState = s.QueuePaused switch
        {
            true => "Paused (operator)",
            false => "Running",
            null => "Unknown",
        };

        if (s.QueueCounts.Count > 0)
        {
            var parts = s.QueueCounts
                .Where(kv => kv.Value > 0 || kv.Key is JobStatus.Pending or JobStatus.Spooled)
                .Select(kv => $"{kv.Key}: {kv.Value}");
            QueueSummary = string.Join(", ", parts);
            var pending = s.QueueCounts.GetValueOrDefault(JobStatus.Pending);
            var uncertain = s.QueueCounts.GetValueOrDefault(JobStatus.UncertainSpooled)
                            + s.QueueCounts.GetValueOrDefault(JobStatus.OutcomeUnknown);
            if (uncertain > 0) QueueSummary += $" — {uncertain} awaiting operator reconciliation";
            if (pending > 0 && s.QueuePaused != true) QueueSummary += " — processing…";
        }
        else
        {
            QueueSummary = s.DatabaseHealthy ? "Queue empty" : "Queue unavailable";
        }

        PrinterSummary = s.PrinterCount > 0
            ? $"{s.PrinterCount} printer(s); selected: {s.SelectedPrinter ?? "(none)"}"
            : "No printers discovered";
        if (s.PrinterIssues.Count > 0)
            PrinterSummary += " — " + string.Join("; ", s.PrinterIssues);

        DiskSummary = s.FreeDiskBytes is { } free
            ? $"{free / 1024.0 / 1024.0:N0} MB free on the data volume"
            : "Unknown";

        LastPrint = s.LastSuccessfulPrintAt?.ToString("yyyy-MM-dd HH:mm 'UTC'") ?? "never";

        Errors = s.Errors.Count > 0 ? string.Join(Environment.NewLine, s.Errors) : "";
    }
}
