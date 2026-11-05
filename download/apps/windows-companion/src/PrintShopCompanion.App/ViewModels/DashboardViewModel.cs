namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;

public partial class DashboardViewModel : ObservableObject
{
    private readonly IPrintShopBackend _backend;
    private readonly IJobQueue _queue;
    private readonly IPrinterService _printers;

    [ObservableProperty]
    private string _shopName = "(not paired)";

    [ObservableProperty]
    private string _deviceId = "";

    [ObservableProperty]
    private string _selectedPrinter = "(none selected)";

    [ObservableProperty]
    private string _connectionStatus = "Connecting…";

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _printingCount;

    [ObservableProperty]
    private int _completedToday;

    [ObservableProperty]
    private int _failedCount;

    [ObservableProperty]
    private ObservableCollection<PrintJobRecord> _recentJobs = new();

    public DashboardViewModel(IPrintShopBackend backend, IJobQueue queue, IPrinterService printers)
    {
        _backend = backend;
        _queue = queue;
        _printers = printers;
        Refresh();
    }

    public void Refresh()
    {
        if (_backend.CurrentShop is { } shop) ShopName = shop.ShopName;
        if (_backend.CurrentDevice is { } dev) DeviceId = dev.DeviceId;
        var sel = _printers.GetSelectedPrinter();
        SelectedPrinter = sel?.DisplayName ?? "(none selected)";
        ConnectionStatus = _backend.IsAuthenticated ? "Connected" : "Not connected";

        var all = _queue.ListAsync().GetAwaiter().GetResult();
        PendingCount = all.Count(j => j.Status == JobStatus.Pending);
        PrintingCount = all.Count(j => j.Status is JobStatus.Downloaded or JobStatus.Spooled);
        CompletedToday = all.Count(j => j.Status is JobStatus.Printed or JobStatus.ReportedPrinted);
        FailedCount = all.Count(j => j.Status == JobStatus.Failed);
        RecentJobs = new ObservableCollection<PrintJobRecord>(all.Take(10));
    }
}
