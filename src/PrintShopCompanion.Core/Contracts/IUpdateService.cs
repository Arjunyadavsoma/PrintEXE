namespace PrintShopCompanion.Core.Contracts;

/// <summary>
/// Checks GitHub Releases for newer versions of the app and can install them.
/// The update source is the PrintEXE GitHub repo's releases API.
/// </summary>
public interface IUpdateService
{
    /// <summary>The currently running app version (from assembly).</summary>
    string CurrentVersion { get; }

    /// <summary>The latest version available on GitHub, or null if not checked / no update.</summary>
    string? LatestVersion { get; }

    /// <summary>True if a newer version is available.</summary>
    bool UpdateAvailable { get; }

    /// <summary>Download URL for the portable ZIP of the latest release.</summary>
    string? DownloadUrl { get; }

    /// <summary>Check GitHub for a newer release. Returns true if an update is available.</summary>
    Task<bool> CheckForUpdatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Download the latest portable ZIP, extract it to a temp dir, write an updater
    /// .bat script, shut down the app, and let the .bat swap files + restart.
    /// </summary>
    Task InstallUpdateAsync(IProgress<int>? progress = null, CancellationToken ct = default);
}
