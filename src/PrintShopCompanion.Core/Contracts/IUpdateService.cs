namespace PrintShopCompanion.Core.Contracts;

/// <summary>
/// The state of the update workflow, surfaced to the UI so the banner can
/// show "Checking", "Downloading", "Verifying", "Installing" etc. instead
/// of just a percentage.
/// </summary>
public enum UpdateState
{
    /// <summary>Not yet checked, or last check found no update.</summary>
    Idle,
    /// <summary>Currently calling the GitHub Releases API.</summary>
    Checking,
    /// <summary>An update is available; waiting for user to click "Update Now".</summary>
    Available,
    /// <summary>Downloading the portable ZIP.</summary>
    Downloading,
    /// <summary>Verifying the SHA-256 of the downloaded bytes against the GitHub asset digest.</summary>
    Verifying,
    /// <summary>Extracting the ZIP and preparing the .bat updater.</summary>
    Extracting,
    /// <summary>The .bat updater is running; the app is about to shut down for file swap.</summary>
    Installing,
    /// <summary>The app is shutting down so the .bat can complete the swap.</summary>
    Restarting,
    /// <summary>The latest available version equals the current version.</summary>
    UpToDate,
    /// <summary>The check or download failed; user can retry.</summary>
    Failed
}

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

    /// <summary>
    /// Optional SHA-256 digest of the latest release's portable ZIP, in the form
    /// "sha256:<hex>". The GitHub Releases API exposes this as the asset's
    /// <c>digest</c> field. Used by <see cref="InstallUpdateAsync"/> to verify
    /// the downloaded bytes match the published artifact.
    /// </summary>
    string? DownloadDigest { get; }

    /// <summary>Optional release notes (markdown body) of the latest release.</summary>
    string? ReleaseNotes { get; }

    /// <summary>Check GitHub for a newer release. Returns true if an update is available.</summary>
    Task<bool> CheckForUpdatesAsync(CancellationToken ct = default);

    /// <summary>
    /// Download the latest portable ZIP, verify its SHA-256 against the GitHub
    /// asset digest, extract it to a temp dir, write an updater .bat script
    /// that backs up the current app dir before xcopy, shut down the app,
    /// and let the .bat swap files + restart. If the new app fails to start,
    /// the .bat restores from the backup.
    /// </summary>
    Task InstallUpdateAsync(IProgress<int>? progress = null, CancellationToken ct = default);

    /// <summary>
    /// Fired whenever the update state changes (Checking -> Downloading ->
    /// Verifying -> Extracting -> Installing -> Restarting). The UI banner
    /// subscribes to this to update its label and progress bar.
    /// </summary>
    event Action<UpdateState>? StateChanged;
}

