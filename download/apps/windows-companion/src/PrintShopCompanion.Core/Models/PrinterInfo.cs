namespace PrintShopCompanion.Core.Models;

/// <summary>
/// A locally known printer. Mirrors what Windows reports via
/// <see cref="System.Printing.PrintQueue"/> but as plain data so the
/// view models and tests don't need to take a hard dependency on WPF.
/// </summary>
public sealed record PrinterInfo
{
    /// <summary>PrintQueue.FullName as Windows knows it (e.g. "Microsoft Print to PDF").</summary>
    public required string FullName { get; init; }

    /// <summary>Display name shown in the UI. Usually PrintQueue.Name or a friendly alias.</summary>
    public required string DisplayName { get; init; }

    /// <summary>True if this is the Windows default printer.</summary>
    public bool IsDefault { get; init; }

    /// <summary>True if Windows reports the printer as offline / not ready.</summary>
    public bool IsOffline { get; init; }

    /// <summary>True if this is a network printer (UNC path, IP, or WSD port).</summary>
    public bool IsNetwork { get; init; }

    /// <summary>Supported paper sizes as reported by the driver.</summary>
    public IReadOnlyList<string> SupportedPaperSizes { get; init; } = Array.Empty<string>();

    /// <summary>Supported duplex modes as reported by the driver.</summary>
    public IReadOnlyList<string> SupportedDuplexModes { get; init; } = Array.Empty<string>();

    /// <summary>True if the driver advertises color capability.</summary>
    public bool SupportsColor { get; init; }

    /// <summary>Last time the companion successfully test-printed to this printer.</summary>
    public DateTimeOffset? LastTestPrintAt { get; init; }

    /// <summary>Whether the shopkeeper has selected this printer for auto-printing.</summary>
    public bool IsSelected { get; init; }
}

/// <summary>Result of a test-print attempt.</summary>
public sealed record TestPrintResult
{
    public required bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public int? SpoolerJobId { get; init; }
    public TimeSpan Duration { get; init; }
}
