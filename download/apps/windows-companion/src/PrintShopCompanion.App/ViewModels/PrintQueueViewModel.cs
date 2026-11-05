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
        Refresh();
    }

    /// <summary>
    /// Operator-driven reprint. Only enabled for jobs in <see cref="JobStatus.UncertainSpooled"/>
    /// or <see cref="JobStatus.OutcomeUnknown"/> — explicitly tells the queue to retry the
    /// job from the beginning. The companion re-acknowledges and re-submits. The queue's
    /// idempotency check is bypassed because we're moving the existing record back to Pending,
    /// not inserting a new one.
    /// </summary>
    [RelayCommand]
    private async Task ReprintAsync()
    {
        if (Selected is null) return;
        if (Selected.Status is not (JobStatus.UncertainSpooled or JobStatus.OutcomeUnknown))
            return;
        // Move back to Pending so the JobProcessor picks it up.
        await _queue.ReleaseAsync(Selected.JobId);
        Refresh();
    }
}

