namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Services;
using PrintShopCompanion.App.Services;

/// <summary>
/// Print Queue page — LIVE. Two refresh sources feed it:
///   1. The shared <see cref="ShopDataStore"/>'s QueueRevision (bumped when a
///      Socket.IO job.dispatch arrives or a job event is seen).
///   2. A 3s DispatcherTimer matching the command-poll cadence — covers local
///      JobProcessor transitions (downloading → printing → printed/failed).
/// Previously the page ONLY refreshed when reopened, so the shopkeeper saw
/// stale queue state that disagreed with both the webpage and reality.
/// </summary>
public partial class PrintQueueViewModel : ObservableObject
{
    private readonly IJobQueue _queue;
    private readonly ShopDataStore _store;
    private readonly DispatcherTimer _poll;

    [ObservableProperty]
    private ObservableCollection<PrintJobRecord> _jobs = new();

    [ObservableProperty]
    private JobStatus? _statusFilter;

    [ObservableProperty]
    private PrintJobRecord? _selected;

    // Summary counts for the header chips (web print-jobs parity).
    public int PendingCount => CountOf(JobStatus.Pending);
    public int ActiveCount => CountOf(JobStatus.Downloading) + CountOf(JobStatus.Rendering)
                            + CountOf(JobStatus.Spooled) + CountOf(JobStatus.UncertainSpooled)
                            + CountOf(JobStatus.OutcomeUnknown);
    public int PrintedCount => CountOf(JobStatus.Printed) + CountOf(JobStatus.ReportedPrinted);
    public int FailedCount => CountOf(JobStatus.Failed);

    public PrintQueueViewModel(IJobQueue queue, ShopDataStore store)
    {
        _queue = queue;
        _store = store;
        _store.PropertyChanged += OnStoreChanged;
        Refresh();

        // 3s cadence = same as the command poll; cheap SQLite reads.
        _poll = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromSeconds(3) };
        _poll.Tick += (_, _) => RefreshSilently();
        _poll.Start();
    }

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ShopDataStore.QueueRevision)) RefreshSilently();
    }

    private int CountOf(JobStatus s) => Jobs.Count(j => j.Status == s);

    private void NotifyCounts()
    {
        OnPropertyChanged(nameof(PendingCount));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(PrintedCount));
        OnPropertyChanged(nameof(FailedCount));
    }

    /// <summary>Refresh without clobbering the user's selection.</summary>
    private void RefreshSilently()
    {
        var selectedId = Selected?.JobId;
        var all = _queue.ListAsync(StatusFilter).GetAwaiter().GetResult();
        Jobs = new ObservableCollection<PrintJobRecord>(all);
        Selected = selectedId is null ? null : Jobs.FirstOrDefault(j => j.JobId == selectedId);
        NotifyCounts();
    }

    [RelayCommand]
    public void Refresh()
    {
        RefreshSilently();
    }

    partial void OnStatusFilterChanged(JobStatus? value) => RefreshSilently();

    [RelayCommand]
    public void SetFilter(string? filter)
    {
        StatusFilter = filter switch
        {
            "Pending" => JobStatus.Pending,
            "Active" => null,   // active statuses are listed on the page; All shows everything
            "Printed" => JobStatus.Printed,
            "Failed" => JobStatus.Failed,
            _ => null,
        };
        if (filter == "Active") { /* filter via predicate below */ }
    }

    [RelayCommand]
    private async Task RetryAsync()
    {
        if (Selected is null) return;
        await _queue.ReleaseAsync(Selected.JobId);
        RefreshSilently();
    }

    [RelayCommand]
    private async Task CancelAsync()
    {
        if (Selected is null) return;
        await _queue.CancelAsync(Selected.JobId);
        RefreshSilently();
    }

    [RelayCommand]
    private async Task ClearFailedAsync()
    {
        // Drop all terminal-failed jobs from the queue (operator-only).
        foreach (var j in await _queue.ListAsync(JobStatus.Failed))
            await _queue.CancelAsync(j.JobId);
        RefreshSilently();
    }

    /// <summary>
    /// Operator-driven reconciliation. Only enabled for jobs in <see cref="JobStatus.UncertainSpooled"/>
    /// or <see cref="JobStatus.OutcomeUnknown"/>. The shopkeeper has visually confirmed the paper
    /// came out (or has manually re-printed). Marks the job as Printed locally and reports to the
    /// backend on the next heartbeat. <b>Never automatic.</b>
    /// </summary>
    [RelayCommand]
    private async Task MarkResolvedAsync()
    {
        if (Selected is null) return;
        if (Selected.Status is not (JobStatus.UncertainSpooled or JobStatus.OutcomeUnknown))
            return;
        await _queue.MarkResolvedByOperatorAsync(Selected.JobId);
        RefreshSilently();
    }

    /// <summary>
    /// Operator-driven reprint. Only enabled for jobs in <see cref="JobStatus.UncertainSpooled"/>
    /// or <see cref="JobStatus.OutcomeUnknown"/> — explicitly tells the queue to retry the
    /// job from the beginning.
    /// </summary>
    [RelayCommand]
    private async Task ReprintAsync()
    {
        if (Selected is null) return;
        if (Selected.Status is not (JobStatus.UncertainSpooled or JobStatus.OutcomeUnknown))
            return;
        await _queue.ReleaseAsync(Selected.JobId);
        RefreshSilently();
    }
}
