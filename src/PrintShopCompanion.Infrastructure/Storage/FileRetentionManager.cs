namespace PrintShopCompanion.Infrastructure.Storage;

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Options;

/// <summary>
/// Automatic local PDF retention manager. Runs on startup + every 60
/// seconds. Deletes PDFs for jobs in a terminal printing state
/// (completed/failed/cancelled) whose <c>LocalDeleteAfter</c> has passed.
/// Also enforces <c>StorageOptions.MaxLocalStorageBytes</c> by
/// aggressively cleaning up the oldest completed files when the Jobs/
/// directory exceeds the quota.
/// <para>Rules:</para>
/// <list type="bullet">
///   <item>Never delete a file referenced by a job in <c>printing</c> state.</item>
///   <item>Never delete a file referenced by a job in <c>queued</c> state.</item>
///   <item>For terminal states: delete if <c>LocalDeleteAfter &lt; now</c>.</item>
///   <item>If the spooler holds a file lock, retry on the next tick.</item>
///   <item>After deletion, set <c>StorageStatus = LocalDeleted</c> in SQLite.</item>
///   <item>The PrintJobRecord row in SQLite is NOT deleted — only the PDF file.</item>
/// </list>
/// </summary>
public sealed class FileRetentionManager : IDisposable
{
    private readonly IJobQueue _queue;
    private readonly LocalStorageLayout _layout;
    private readonly IOptionsMonitor<StorageOptions> _opts;
    private readonly ILogger<FileRetentionManager> _logger;
    private Timer? _timer;
    private CancellationTokenSource? _cts;
    private long _totalCleanedUpBytes;
    private int _totalCleanedUpFiles;
    private DateTimeOffset? _lastCleanupAt;

    public event Action<int, long>? CleanupCompleted;  // (filesDeleted, bytesFreed)

    public FileRetentionManager(
        IJobQueue queue,
        LocalStorageLayout layout,
        IOptionsMonitor<StorageOptions> opts,
        ILogger<FileRetentionManager> logger)
    {
        _queue = queue;
        _layout = layout;
        _opts = opts;
        _logger = logger;
    }

    public void Start(CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        // Run once immediately on startup (catch up from a crash).
        _ = Task.Run(() => RunCleanupAsync(_cts.Token), _cts.Token);
        // Then every 60 seconds.
        _timer = new Timer(_ => _ = Task.Run(() => RunCleanupAsync(_cts?.Token ?? CancellationToken.None)),
            null, TimeSpan.FromSeconds(60), TimeSpan.FromSeconds(60));
    }

    public async Task StopAsync()
    {
        _timer?.Dispose();
        _timer = null;
        try { _cts?.Cancel(); } catch { }
        _cts?.Dispose();
        _cts = null;
        await Task.CompletedTask;
    }

    private async Task RunCleanupAsync(CancellationToken ct)
    {
        try
        {
            var now = DateTimeOffset.UtcNow;
            var deletedFiles = 0;
            var freedBytes = 0L;

            // 1. Delete files whose localDeleteAfter has passed.
            var jobs = await _queue.ListAsync(ct: ct);
            foreach (var job in jobs)
            {
                if (ct.IsCancellationRequested) break;
                if (job.Status is not (JobStatus.Printed or JobStatus.ReportedPrinted or JobStatus.Failed or JobStatus.Cancelled))
                    continue;  // not terminal
                if (job.LocalDeleteAfter is null || job.LocalDeleteAfter > now)
                    continue;  // not yet time
                if (string.IsNullOrEmpty(job.LocalPdfPath) || !File.Exists(job.LocalPdfPath))
                    continue;  // already deleted or never downloaded

                try
                {
                    var fileSize = new FileInfo(job.LocalPdfPath).Length;
                    File.Delete(job.LocalPdfPath);
                    deletedFiles++;
                    freedBytes += fileSize;
                    _totalCleanedUpFiles++;
                    _totalCleanedUpBytes += fileSize;
                    _logger.LogInformation("Deleted local PDF for job {JobId} ({Bytes} bytes, retention deadline {Deadline})",
                        job.JobId, fileSize, job.LocalDeleteAfter);
                }
                catch (IOException ex)
                {
                    // File may be locked by the spooler — retry on the next tick.
                    _logger.LogDebug(ex, "File {Path} is locked; will retry on next tick", job.LocalPdfPath);
                }
                catch (UnauthorizedAccessException ex)
                {
                    _logger.LogWarning(ex, "Access denied deleting {Path}", job.LocalPdfPath);
                }
            }

            // 2. Enforce MaxLocalStorageBytes — aggressively clean up oldest completed files.
            var maxBytes = _opts.CurrentValue.MaxLocalStorageBytes;
            var jobsDirSize = GetDirectorySize(_layout.JobsIncomingDir)
                             + GetDirectorySize(_layout.JobsQueuedDir)
                             + GetDirectorySize(_layout.JobsPrintingDir)
                             + GetDirectorySize(_layout.JobsCompletedDir);
            if (jobsDirSize > maxBytes)
            {
                _logger.LogWarning("Jobs/ directory is {Bytes} bytes (max {MaxBytes}) — aggressively cleaning up oldest completed files",
                    jobsDirSize, maxBytes);
                var completedJobs = jobs
                    .Where(j => j.Status is JobStatus.Printed or JobStatus.ReportedPrinted or JobStatus.Failed or JobStatus.Cancelled)
                    .Where(j => !string.IsNullOrEmpty(j.LocalPdfPath) && File.Exists(j.LocalPdfPath))
                    .OrderBy(j => j.PrintedAt ?? j.UpdatedAt)
                    .ToList();
                foreach (var job in completedJobs)
                {
                    if (jobsDirSize <= maxBytes) break;
                    try
                    {
                        var fileSize = new FileInfo(job.LocalPdfPath!).Length;
                        File.Delete(job.LocalPdfPath!);
                        deletedFiles++;
                        freedBytes += fileSize;
                        jobsDirSize -= fileSize;
                        _logger.LogWarning("Aggressively deleted {Path} ({Bytes} bytes) due to disk quota", job.LocalPdfPath, fileSize);
                    }
                    catch (Exception ex) { _logger.LogDebug(ex, "Aggressive cleanup failed for {Path}", job.LocalPdfPath); }
                }
            }

            _lastCleanupAt = now;
            if (deletedFiles > 0)
            {
                CleanupCompleted?.Invoke(deletedFiles, freedBytes);
                _logger.LogInformation("Retention cleanup: deleted {Count} files, freed {Bytes} bytes", deletedFiles, freedBytes);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Retention cleanup failed");
        }
    }

    private static long GetDirectorySize(string path)
    {
        if (!Directory.Exists(path)) return 0;
        return Directory.GetFiles(path, "*", SearchOption.AllDirectories)
            .Sum(f => { try { return new FileInfo(f).Length; } catch { return 0; } });
    }

    /// <summary>Total bytes freed since the manager started.</summary>
    public long TotalCleanedUpBytes => _totalCleanedUpBytes;

    /// <summary>Total files deleted since the manager started.</summary>
    public int TotalCleanedUpFiles => _totalCleanedUpFiles;

    /// <summary>Last time the cleanup ran.</summary>
    public DateTimeOffset? LastCleanupAt => _lastCleanupAt;

    /// <summary>Current size of the Jobs/ directory in bytes.</summary>
    public long CurrentJobsDirSize =>
        GetDirectorySize(_layout.JobsIncomingDir)
        + GetDirectorySize(_layout.JobsQueuedDir)
        + GetDirectorySize(_layout.JobsPrintingDir)
        + GetDirectorySize(_layout.JobsCompletedDir);

    public void Dispose()
    {
        _timer?.Dispose();
        _cts?.Dispose();
    }
}
