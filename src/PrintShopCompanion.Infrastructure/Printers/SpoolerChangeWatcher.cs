namespace PrintShopCompanion.Infrastructure.Printers;

using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;

/// <summary>
/// Watches the Windows print spooler for changes and raises .NET events —
/// instead of polling. v0.7.17.
///
/// Technique adapted (behavior-level, no code) from QZ Tray's
/// WmiPrinterStatusThread / WmiPrinterConnectionsThread: one OS change
/// notification handle on the local print server, a dedicated waiting
/// thread, and a debounced "printer set changed" signal for the UI.
/// The API surface (FindFirstPrinterChangeNotification /
/// FindNextPrinterChangeNotification / WaitForSingleObject) is public
/// Microsoft-documented winspool.drv functionality. See
/// docs/research/OPEN_SOURCE_FEATURE_EVIDENCE_MATRIX.md (A4/A5) and
/// the license audit (LGPL source; API pattern only, fresh code).
///
/// Seams kept testable: the wait loop calls the virtual
/// <see cref="WaitForSignal"/> and <see cref="ReadChange"/> hooks, so unit
/// tests can drive the state machine without a real spooler (simulated —
/// honestly labeled; no hardware events are claimed).
/// </summary>
public class SpoolerChangeWatcher : IDisposable
{
    private readonly ILogger<SpoolerChangeWatcher>? _logger;
    private Thread? _thread;
    private CancellationTokenSource? _cts;
    private readonly object _startLock = new();
    private string[] _lastPrinterSet = Array.Empty<string>();
    private DateTimeOffset _lastSetSignal = DateTimeOffset.MinValue;
    private static readonly TimeSpan SetDebounce = TimeSpan.FromSeconds(2);

    /// <summary>Fired when the set of installed printers changes (add/remove/rename). Args: current names.</summary>
    public event Action<IReadOnlyList<string>>? PrintersChanged;

    /// <summary>Fired on any spooler job or printer-status change (the cheap, coarse signal).</summary>
    public event Action? SpoolerActivity;

    public SpoolerChangeWatcher(ILogger<SpoolerChangeWatcher>? logger = null)
    {
        _logger = logger;
    }

    public bool IsRunning { get; private set; }

    /// <summary>Starts watching. Safe to call multiple times; only the first starts a thread.</summary>
    public void Start()
    {
        lock (_startLock)
        {
            if (IsRunning) return;
            if (!OperatingSystem.IsWindows()) return; // No-op elsewhere (tests run on Linux CI legs).
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;
            _thread = new Thread(() => RunLoop(ct))
            {
                IsBackground = true,
                Name = "SpoolerChangeWatcher",
            };
            _thread.Start();
            IsRunning = true;
        }
    }

    /// <summary>Stops watching and releases the notification handle.</summary>
    public void Stop()
    {
        lock (_startLock)
        {
            if (!IsRunning) return;
            _cts?.Cancel();
            _thread?.Join(TimeSpan.FromSeconds(3));
            _cts?.Dispose();
            _cts = null;
            _thread = null;
            IsRunning = false;
        }
    }

    // ─────────────────── wait loop (virtual seams for tests) ───────────────────

    internal void RunLoop(CancellationToken ct)
    {
        var handle = OpenWatcher();
        if (handle == IntPtr.Zero)
        {
            _logger?.LogWarning("Spooler change notification could not be opened (GetLastError={Error}); watcher idle", Marshal.GetLastWin32Error());
            return;
        }
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var signaled = WaitForSignal(handle, ct);
                if (!signaled) break; // cancelled

                // Consume the change record so the handle re-arms.
                ReadChange(handle);

                SpoolerActivity?.Invoke();

                // Debounced printer-set comparison: the spooler fires many
                // job-level changes for one physical add/remove; only tell
                // the UI when the ENUMERATED set actually differs (QZ Tray
                // relaunches listeners on set change; we just refresh UI).
                if (PrintersChanged is not null &&
                    DateTimeOffset.UtcNow - _lastSetSignal > SetDebounce)
                {
                    var current = EnumeratePrinterNames();
                    if (!current.SequenceEqual(_lastPrinterSet))
                    {
                        _lastPrinterSet = current;
                        _lastSetSignal = DateTimeOffset.UtcNow;
                        try { PrintersChanged.Invoke(current); }
                        catch (Exception ex) { _logger?.LogError(ex, "PrintersChanged handler threw"); }
                    }
                }
            }
        }
        finally
        {
            ClosePrinter(handle);
        }
    }

    /// <summary>Waits for a change or cancellation. Virtual seam (tests override).</summary>
    internal virtual bool WaitForSignal(IntPtr handle, CancellationToken ct)
    {
        const int WAIT_OBJECT_0 = 0;
        const int WAIT_TIMEOUT = 0x102;
        while (!ct.IsCancellationRequested)
        {
            // Wait in slices so cancellation is responsive.
            var r = WaitForSingleObject(handle, 500);
            if (r == WAIT_OBJECT_0) return true;
            if (r != WAIT_TIMEOUT) return false; // failed — stop cleanly
        }
        return false;
    }

    /// <summary>Consumes + resets the change record. Virtual seam (tests override).</summary>
    internal virtual void ReadChange(IntPtr handle)
    {
        _ = FindNextPrinterChangeNotification(handle, out _, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>Enumerates installed printer names (server-level). Virtual seam.</summary>
    internal virtual string[] EnumeratePrinterNames()
    {
        try
        {
            var server = new System.Printing.LocalPrintServer();
            var queues = server.GetPrintQueues(new[] { System.Printing.EnumeratedPrintQueueTypes.Local, System.Printing.EnumeratedPrintQueueTypes.Connections });
            return queues.Select(q => q.FullName).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "Printer enumeration in watcher failed");
            return _lastPrinterSet;
        }
    }

    private IntPtr OpenWatcher()
    {
        // Watch the server for BOTH printer and job changes — the same
        // coarse signal QZ Tray splits into per-printer threads. We do not
        // need per-printer granularity: any change re-checks the set.
        const uint PRINTER_CHANGE_PRINTER = 0x00000070; // add/delete/set of printers
        const uint PRINTER_CHANGE_JOB = 0x0000FF00;     // all job activity
        var flags = PRINTER_CHANGE_PRINTER | PRINTER_CHANGE_JOB;
        return FindFirstPrinterChangeNotification(PrinterEnum.Local | PrinterEnum.Connections, flags, 0, IntPtr.Zero);
    }

    public void Dispose() => Stop();

    // ─────────────────────────── winspool.drv P/Invoke ───────────────────────────
    // Signatures per Microsoft Learn (FindFirstPrinterChangeNotificationW et al.).

    [Flags]
    internal enum PrinterEnum : uint
    {
        Local = 0x00000002,
        Connections = 0x00000004,
    }

    [DllImport("winspool.drv", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "FindFirstPrinterChangeNotificationW")]
    private static extern IntPtr FindFirstPrinterChangeNotification(PrinterEnum flags, uint fdwFilter, uint fdwOptions, IntPtr pPrinterNotifyOptions);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool FindNextPrinterChangeNotification(IntPtr hChange, out uint pdwChange, IntPtr pNotifyOptions, IntPtr ppPrinterNotifyInfo);

    [DllImport("winspool.drv", SetLastError = true)]
    private static extern bool FindClosePrinterChangeNotification(IntPtr hChange);

    // We never opened a printer handle with OpenPrinter (server-level watch),
    // so FindClosePrinterChangeNotification is the correct release call.
    private static void ClosePrinter(IntPtr handle)
    {
        if (handle != IntPtr.Zero) _ = FindClosePrinterChangeNotification(handle);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
}
