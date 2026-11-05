namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;

public partial class PrintQueueViewModel : ObservableObject
{
    private readonly IJobQueue _queue;

    [ObservableProperty]
    private ObservableCollection<PrintJobRecord> _jobs = new();

    [ObservableProperty]
    private JobStatus? _statusFilter;

    [ObservableProperty]
    private PrintJobRecord? _selected;

    public PrintQueueViewModel(IJobQueue queue)
    {
        _queue = queue;
        Refresh();
    }

    [RelayCommand]
    public void Refresh()
    {
        var all = _queue.ListAsync(StatusFilter).GetAwaiter().GetResult();
        Jobs = new ObservableCollection<PrintJobRecord>(all);
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (Selected is null) return;
        await _queue.ReleaseAsync(Selected.JobId);
        Refresh();
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (Selected is null) return;
        await _queue.CancelAsync(Selected.JobId);
        Refresh();
    }

    [RelayCommand]
    private async Task ClearFailedAsync()
    {
        // Drop all terminal-failed jobs from the queue (operator-only).
        foreach (var j in await _queue.ListAsync(JobStatus.Failed))
            await _queue.CancelAsync(j.JobId);
        Refresh();
    }
}
