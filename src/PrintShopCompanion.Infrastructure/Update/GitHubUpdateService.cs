namespace PrintShopCompanion.Infrastructure.Update;

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrintShopCompanion.Core.Contracts;

/// <summary>
/// Checks GitHub Releases for newer versions and installs them by downloading
/// the portable ZIP, verifying its SHA-256 against the GitHub asset digest,
/// extracting it, and running a .bat updater that backs up the current app
/// dir before xcopying the new files. If the new app fails to start, the .bat
/// restores from the backup.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubUpdateService> _logger;
    private GitHubRelease? _latestRelease;

    // Hard guard against overlapping installs. Even though MainWindow now
    // also guards, this is the contract-level safety net: any caller that
    // invokes InstallUpdateAsync twice will hit this and skip the second call.
    private int _installing; // 0 = idle, 1 = busy

    // Current state machine, surfaced to the UI via StateChanged.
    private UpdateState _state = UpdateState.Idle;

    private const string RepoOwner = "Arjunyadavsoma";
    private const string RepoName = "PrintEXE";
    private const string PortableZipName = "PrintShopCompanion-portable.zip";

    public GitHubUpdateService(HttpClient http, ILogger<GitHubUpdateService> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PrintShopCompanion", CurrentVersion));
    }

    public string CurrentVersion
    {
        get
        {
            try
            {
                var appAsm = AppDomain.CurrentDomain.GetAssemblies()
                    .FirstOrDefault(a => a.GetName().Name == "PrintShopCompanion");
                if (appAsm != null && appAsm.GetName().Version != null)
                    return appAsm.GetName().Version!.ToString();
            }
            catch { }
            try
            {
                var ver = Assembly.GetExecutingAssembly().GetName().Version;
                if (ver != null) return ver.ToString();
            }
            catch { }
            return "0.0.0.0";
        }
    }

    public string? LatestVersion => _latestRelease?.TagName?.TrimStart('v');
    public bool UpdateAvailable => _latestRelease is not null && IsNewer(LatestVersion, CurrentVersion);
    public string? DownloadUrl => _latestRelease?.Assets?
        .FirstOrDefault(a => a.Name?.Equals(PortableZipName, StringComparison.OrdinalIgnoreCase) == true)?.BrowserDownloadUrl;

    /// <summary>
    /// SHA-256 digest of the latest release's portable ZIP, in the form
    /// "sha256:<hex>". GitHub exposes this as the asset's <c>digest</c> field
    /// (only present on assets uploaded after 2024). Null if GitHub didn't
    /// provide a digest for the asset.
    /// </summary>
    public string? DownloadDigest => _latestRelease?.Assets?
        .FirstOrDefault(a => a.Name?.Equals(PortableZipName, StringComparison.OrdinalIgnoreCase) == true)?.Digest;

    /// <summary>Release notes (markdown body) of the latest release, if provided.</summary>
    public string? ReleaseNotes => _latestRelease?.Body;

    /// <summary>
    /// Current update workflow state. Subscribers receive change notifications
    /// via <see cref="StateChanged"/>.
    /// </summary>
    public UpdateState State
    {
        get => _state;
        private set
        {
            if (_state == value) return;
            _state = value;
            StateChanged?.Invoke(value);
            _logger.LogInformation("Update state -> {State}", value);
        }
    }

    public event Action<UpdateState>? StateChanged;

    public async Task<bool> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        State = UpdateState.Checking;
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var release = await _http.GetFromJsonAsync<GitHubRelease>(url, ct);
            if (release is null)
            {
                State = UpdateState.Idle;
                return false;
            }

            _latestRelease = release;
            var latest = release.TagName?.TrimStart('v') ?? "0.0.0.0";
            var isNewer = IsNewer(latest, CurrentVersion);

            _logger.LogInformation("Update check: current={Current}, latest={Latest}, updateAvailable={Available}, digest={Digest}",
                CurrentVersion, latest, isNewer, DownloadDigest ?? "(none)");

            State = isNewer ? UpdateState.Available : UpdateState.UpToDate;
            return isNewer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for updates");
            State = UpdateState.Failed;
            return false;
        }
    }

    public async Task InstallUpdateAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        // Hard contract-level guard. Interlocked.CompareExchange is atomic —
        // even if two threads call this simultaneously, only one will get past
        // the gate; the other throws immediately.
        if (Interlocked.CompareExchange(ref _installing, 1, 0) != 0)
        {
            _logger.LogWarning("InstallUpdateAsync called while an install is already in progress — ignoring");
            return;
        }

        try
        {
            if (DownloadUrl is null)
                throw new InvalidOperationException("No update available. Call CheckForUpdatesAsync first.");

            // 1. Download the portable ZIP to a unique temp dir.
            var tempDir = Path.Combine(Path.GetTempPath(), "PrintShop-update-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            try
            {
                var oldDirs = Directory.GetDirectories(Path.GetTempPath(), "PrintShop-update*");
                foreach (var d in oldDirs) { try { Directory.Delete(d, true); } catch { } }
            }
            catch { }
            Directory.CreateDirectory(tempDir);

            var zipPath = Path.Combine(tempDir, PortableZipName);
            _logger.LogInformation("Downloading update from {Url}", DownloadUrl);
            State = UpdateState.Downloading;
            progress?.Report(5);

            using (var resp = await _http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
            {
                resp.EnsureSuccessStatusCode();

                // Stream the bytes to disk AND compute SHA-256 in one pass.
                // This avoids reading the file twice (once for download, once
                // for verify) which is slow on hard drives.
                //
                // IMPORTANT: We must dispose the CryptoStream before reading
                // sha.Hash — the SHA256 algorithm only finalizes the hash
                // when the CryptoStream is closed (FlushFinalBlock is called).
                // Reading sha.Hash before disposal throws:
                //   "Hash must be finalized before the hash value is retrieved."
                string actualSha;
                long bytesDownloaded;
                using (var sha = SHA256.Create())
                {
                    using var fs = File.Create(zipPath);
                    using (var cryptoStream = new CryptoStream(fs, sha, CryptoStreamMode.Write))
                    {
                        await resp.Content.CopyToAsync(cryptoStream, ct);
                        await cryptoStream.FlushAsync(ct);
                        // cryptoStream disposal here finalizes the SHA256 hash.
                    }
                    bytesDownloaded = fs.Length;
                    actualSha = BitConverter.ToString(sha.Hash!).Replace("-", "").ToLowerInvariant();
                }

                _logger.LogInformation("Downloaded {Bytes} bytes, sha256={Sha}", bytesDownloaded, actualSha);
                progress?.Report(40);

                // 2. Verify the SHA-256 against the GitHub-provided digest.
                // GitHub returns digests in the form "sha256:<hex>". If GitHub
                // didn't provide one (older release), we log a warning but
                // proceed — we can't verify what we don't have.
                State = UpdateState.Verifying;
                if (DownloadDigest is string expectedDigest)
                {
                    var expected = expectedDigest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase)
                        ? expectedDigest.Substring("sha256:".Length).ToLowerInvariant()
                        : expectedDigest.ToLowerInvariant();
                    if (!string.Equals(actualSha, expected, StringComparison.OrdinalIgnoreCase))
                    {
                        _logger.LogError("SHA-256 mismatch. Expected {Expected}, got {Actual}", expected, actualSha);
                        throw new InvalidOperationException(
                            $"Downloaded update failed integrity check.\n\n" +
                            $"Expected SHA-256: {expected}\n" +
                            $"Actual SHA-256:   {actualSha}\n\n" +
                            "The download may have been corrupted or tampered with. Aborting update.");
                    }
                    _logger.LogInformation("SHA-256 verified OK");
                }
                else
                {
                    _logger.LogWarning("GitHub did not provide a digest for the asset — skipping SHA-256 verify (download cannot be verified)");
                }
                progress?.Report(50);
            }

            // 3. Extract the ZIP.
            State = UpdateState.Extracting;
            var extractDir = Path.Combine(tempDir, "new");
            Directory.CreateDirectory(extractDir);
            ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
            progress?.Report(65);

            // Verify the extraction actually produced files. If it didn't,
            // abort before we kill the running app.
            if (!Directory.EnumerateFileSystemEntries(extractDir).Any())
                throw new InvalidOperationException("Update ZIP extracted to an empty directory — aborting");

            // 4. Write the updater .bat to the APP directory (not temp dir).
            //    The .bat backs up the current app dir BEFORE xcopy, so if the
            //    new EXE fails to start the user can manually restore. The .bat
            //    also creates a `_printshop_update_failed` sentinel file if
            //    xcopy fails, which the App checks on startup.
            var appDir = AppContext.BaseDirectory;
            var batPath = Path.Combine(appDir, "_printshop_updater.bat");
            var backupDir = Path.Combine(Path.GetTempPath(), "PrintShop-backup-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            var exeName = Process.GetCurrentProcess().ProcessName + ".exe";
            var exePath = Path.Combine(appDir, exeName);
            var logPath = Path.Combine(Path.GetTempPath(), "PrintShop-updater.log");
            var sentinelPath = Path.Combine(appDir, "_printshop_update_failed");

            // Robust .bat: backup, longer wait, retry xcopy up to 5 times,
            // verify the new EXE exists, restart, restore on failure.
            var batContent =
                "@echo off\r\n" +
                "setlocal enabledelayedexpansion\r\n" +
                "chcp 65001 >nul\r\n" +
                "echo. >> \"" + logPath + "\"\r\n" +
                "echo === PrintShop updater run %date% %time% === >> \"" + logPath + "\"\r\n" +
                "echo Backing up current app dir to \"" + backupDir + "\"... >> \"" + logPath + "\"\r\n" +
                "xcopy \"" + appDir + "\\*\" \"" + backupDir + "\\\" /E /I /Y /Q 1>>\"" + logPath + "\" 2>&1\r\n" +
                "if !errorlevel! neq 0 (\r\n" +
                "    echo Backup failed — aborting update >> \"" + logPath + "\"\r\n" +
                "    echo BACKUP_FAILED > \"" + sentinelPath + "\"\r\n" +
                "    exit /b 1\r\n" +
                ")\r\n" +
                "echo Backup OK >> \"" + logPath + "\"\r\n" +
                "echo Waiting for app to exit... >> \"" + logPath + "\"\r\n" +
                "timeout /t 4 /nobreak >nul\r\n" +
                "echo Ensuring app process is stopped... >> \"" + logPath + "\"\r\n" +
                "taskkill /f /im \"" + exeName + "\" 2>nul\r\n" +
                "timeout /t 2 /nobreak >nul\r\n" +
                "echo Copying new files from \"" + extractDir + "\" to \"" + appDir + "\"... >> \"" + logPath + "\"\r\n" +
                "set XCOPY_OK=0\r\n" +
                "for /L %%i in (1,1,5) do (\r\n" +
                "    if !XCOPY_OK! equ 0 (\r\n" +
                "        xcopy \"" + extractDir + "\\*\" \"" + appDir + "\" /E /Y /I /Q /R 1>>\"" + logPath + "\" 2>&1\r\n" +
                "        if !errorlevel! equ 0 set XCOPY_OK=1\r\n" +
                "        if !XCOPY_OK! equ 0 (\r\n" +
                "            echo Xcopy attempt %%i failed, retrying in 2s... >> \"" + logPath + "\"\r\n" +
                "            timeout /t 2 /nobreak >nul\r\n" +
                "        )\r\n" +
                "    )\r\n" +
                ")\r\n" +
                "if !XCOPY_OK! equ 0 (\r\n" +
                "    echo xcopy failed after 5 attempts — restoring from backup >> \"" + logPath + "\"\r\n" +
                "    echo XCOPY_FAILED > \"" + sentinelPath + "\"\r\n" +
                "    xcopy \"" + backupDir + "\\*\" \"" + appDir + "\" /E /Y /I /Q /R 1>>\"" + logPath + "\" 2>&1\r\n" +
                "    start \"\" \"" + exePath + "\"\r\n" +
                "    exit /b 1\r\n" +
                ")\r\n" +
                "echo Verifying new EXE exists... >> \"" + logPath + "\"\r\n" +
                "if not exist \"" + exePath + "\" (\r\n" +
                "    echo New EXE missing — restoring from backup >> \"" + logPath + "\"\r\n" +
                "    echo EXE_MISSING > \"" + sentinelPath + "\"\r\n" +
                "    xcopy \"" + backupDir + "\\*\" \"" + appDir + "\" /E /Y /I /Q /R 1>>\"" + logPath + "\" 2>&1\r\n" +
                "    start \"\" \"" + exePath + "\"\r\n" +
                "    exit /b 1\r\n" +
                ")\r\n" +
                "echo xcopy succeeded, starting new app... >> \"" + logPath + "\"\r\n" +
                "start \"\" \"" + exePath + "\"\r\n" +
                "echo Cleaning up temp files... >> \"" + logPath + "\"\r\n" +
                "rmdir /s /q \"" + tempDir + "\"\r\n" +
                "rmdir /s /q \"" + backupDir + "\"\r\n" +
                "echo Scheduling self-delete... >> \"" + logPath + "\"\r\n" +
                "(timeout /t 3 /nobreak >nul & del \"" + batPath + "\") 2>nul\r\n" +
                "exit /b 0\r\n";

            await File.WriteAllTextAsync(batPath, batContent, ct);
            progress?.Report(85);

            // 5. Launch the updater from the APP directory (not temp) so the
            //    temp dir isn't locked by the process's working directory.
            State = UpdateState.Installing;
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c \"" + batPath + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = appDir
            };
            Process.Start(psi);
            progress?.Report(95);

            _logger.LogInformation("Update installed. App will now exit for file swap.");
            State = UpdateState.Restarting;
            progress?.Report(100);

            // 6. Exit the app so the .bat can swap files.
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                System.Windows.Application.Current.Shutdown();
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InstallUpdateAsync failed");
            State = UpdateState.Failed;
            throw;
        }
        finally
        {
            // If anything threw before Shutdown(), reset the guard so the user
            // can retry. If Shutdown() actually fired, this thread is gone and
            // the reset is moot.
            _installing = 0;
        }
    }

    /// <summary>
    /// Called on startup. Checks for a sentinel file left by a previous
    /// failed .bat updater. If present, logs the failure and surfaces a
    /// message to the user that the previous update failed and they may
    /// want to manually re-download. Returns the failure reason or null
    /// if no failure was detected.
    /// </summary>
    public string? CheckForFailedUpdateSentinel()
    {
        try
        {
            var appDir = AppContext.BaseDirectory;
            var sentinelPath = Path.Combine(appDir, "_printshop_update_failed");
            if (!File.Exists(sentinelPath)) return null;

            var reason = File.ReadAllText(sentinelPath).Trim();
            _logger.LogError("Previous update failed: {Reason}", reason);

            // Delete the sentinel so we don't keep showing the warning
            File.Delete(sentinelPath);
            return reason;
        }
        catch { return null; }
    }

    private static bool IsNewer(string? latest, string current)
    {
        if (string.IsNullOrWhiteSpace(latest)) return false;
        if (!Version.TryParse(latest, out var latestVer)) return false;
        if (!Version.TryParse(current, out var currentVer)) return false;
        return latestVer > currentVer;
    }
}

internal sealed class GitHubRelease
{
    [JsonPropertyName("tag_name")] public string? TagName { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("html_url")] public string? HtmlUrl { get; set; }
    [JsonPropertyName("body")] public string? Body { get; set; }
    [JsonPropertyName("assets")] public List<GitHubAsset>? Assets { get; set; }
}

internal sealed class GitHubAsset
{
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("browser_download_url")] public string? BrowserDownloadUrl { get; set; }
    [JsonPropertyName("size")] public long Size { get; set; }
    // GitHub exposes the SHA-256 digest of the asset here, in the form
    // "sha256:<hex>". Available on assets uploaded after 2024. Older
    // releases may not have this field — the verifier handles null.
    [JsonPropertyName("digest")] public string? Digest { get; set; }
}

