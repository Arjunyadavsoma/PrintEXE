namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;

/// <summary>
/// Printers screen. Lists all installed Windows printers, lets the shopkeeper
/// pick one as the default, and run a test print.
/// </summary>
public partial class PrintersViewModel : ObservableObject
{
    private readonly IPrinterService _printSvc;
    private readonly IOptionsMonitor<PrinterOptions> _options;

    [ObservableProperty]
    private ObservableCollection<PrinterInfo> _printers = new();

    [ObservableProperty]
    private PrinterInfo? _selected;

    [ObservableProperty]
    private string _testStatus = "";

    [ObservableProperty]
    private bool _isTestPrinting;

    public PrintersViewModel(IPrinterService printers, IOptionsMonitor<PrinterOptions> options)
    {
        _printSvc = printers;
        _options = options;
        Refresh();
    }

    public void Refresh()
    {
        Printers = new ObservableCollection<PrinterInfo>(_printSvc.DiscoverPrinters());
        Selected = Printers.FirstOrDefault(p => p.IsSelected)
                ?? Printers.FirstOrDefault(p => p.IsDefault)
                ?? Printers.FirstOrDefault();
    }

    [RelayCommand]
    private async Task SelectPrinterAsync()
    {
        if (Selected is null) return;
        await _printSvc.SelectPrinterAsync(Selected.FullName);
        TestStatus = $"Selected {Selected.DisplayName}.";
    }

    [RelayCommand]
    public async Task TestPrintAsync()
    {
        if (Selected is null) return;
        IsTestPrinting = true;
        TestStatus = "Sending test print…";
        try
        {
            var result = await _printSvc.TestPrintAsync(Selected.FullName);
            TestStatus = result.Success
                ? $"Test print sent to {Selected.DisplayName} (spooler job #{result.SpoolerJobId}, {result.Duration.TotalSeconds:F1}s)."
                : $"Test print failed: {result.ErrorMessage}";
        }
        finally { IsTestPrinting = false; }
    }

    [RelayCommand]
    private void RefreshList() => Refresh();

    /// <summary>
    /// v0.7.17: called by the spooler change watcher (via the view, on the
    /// UI dispatcher) whenever the installed-printer set changes, so the
    /// list stays current without a manual refresh. The watcher itself
    /// lives in Infrastructure (SpoolerChangeWatcher) — technique adapted
    /// from QZ Tray's connections watchdog (see
    /// docs/research/OPEN_SOURCE_FEATURE_EVIDENCE_MATRIX.md A5).
    /// </summary>
    public void RefreshFromSpoolerChange()
    {
        // Preserve the operator's selection across the refresh when the
        // printer still exists; Refresh() re-selects it via IsSelected.
        var keep = Selected?.FullName;
        Refresh();
        if (keep is not null)
            Selected = Printers.FirstOrDefault(p => p.FullName == keep) ?? Selected;
    }
}
