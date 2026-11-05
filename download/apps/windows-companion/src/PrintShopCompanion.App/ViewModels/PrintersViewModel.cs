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
    private readonly IPrinterService _printers;
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
        _printers = printers;
        _options = options;
        Refresh();
    }

    public void Refresh()
    {
        Printers = new ObservableCollection<PrinterInfo>(_printers.DiscoverPrinters());
        Selected = Printers.FirstOrDefault(p => p.IsSelected)
                ?? Printers.FirstOrDefault(p => p.IsDefault)
                ?? Printers.FirstOrDefault();
    }

    [RelayCommand]
    private async Task SelectPrinterAsync()
    {
        if (Selected is null) return;
        await _printers.SelectPrinterAsync(Selected.FullName);
        TestStatus = $"Selected {Selected.DisplayName}.";
    }

    [RelayCommand]
    private async Task TestPrintAsync()
    {
        if (Selected is null) return;
        IsTestPrinting = true;
        TestStatus = "Sending test print…";
        try
        {
            var result = await _printers.TestPrintAsync(Selected.FullName);
            TestStatus = result.Success
                ? $"Test print sent to {Selected.DisplayName} (spooler job #{result.SpoolerJobId}, {result.Duration.TotalSeconds:F1}s)."
                : $"Test print failed: {result.ErrorMessage}";
        }
        finally { IsTestPrinting = false; }
    }

    [RelayCommand]
    private void RefreshList() => Refresh();
}
