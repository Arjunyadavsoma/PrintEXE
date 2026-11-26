namespace PrintShopCompanion.Core.Printers;

using Models;

/// <summary>
/// Maps raw System.Printing status bits to the coarse <see cref="PrinterStatus"/>
/// vocabulary. Pure logic so it is unit-testable without a spooler.
/// v0.7.17 — technique adapted from PrintHub's Win32_Printer status mapping
/// (MIT): offline wins over error, error over busy, busy over ready, and a
/// paused queue reads as "Busy" because an operator can act on it.
/// docs/research/OPEN_SOURCE_FEATURE_EVIDENCE_MATRIX.md (A3).
/// </summary>
public static class PrinterStatusMapper
{
    /// <summary>Single source for the UI sentence per status.</summary>
    public static string TextFor(PrinterStatus status) => status switch
    {
        PrinterStatus.Ready => "Ready",
        PrinterStatus.Busy => "Busy or paused",
        PrinterStatus.Error => "Error — check paper, toner and jams",
        PrinterStatus.Offline => "Offline — check power and connection",
        _ => "Status unknown",
    };

    /// <summary>
    /// Precedence: Offline &gt; Error &gt; Busy &gt; Ready &gt; Unknown.
    /// All flags are pre-extracted booleans so tests (and non-Windows
    /// callers) never touch PrintQueueStatus.
    /// </summary>
    public static PrinterStatus Map(
        bool isOffline,
        bool isPaused,
        bool isError,
        bool isBusy,
        bool isPrinting,
        bool isInitializing,
        bool isPendingDeletion) => (isOffline, isError, isPaused, isBusy || isPrinting || isInitializing) switch
    {
        (true, _, _, _) => PrinterStatus.Offline,
        (_, true, _, _) => PrinterStatus.Error,
        (_, _, true, _) => PrinterStatus.Busy,
        (_, _, _, true) => PrinterStatus.Busy,
        _ => PrinterStatus.Ready,
    };
}
