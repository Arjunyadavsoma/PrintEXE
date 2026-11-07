namespace PrintShopCompanion.Infrastructure.Update;

using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using PrintShopCompanion.Core.Contracts;

/// <summary>
/// Checks GitHub Releases for newer versions and installs them by downloading
/// the portable ZIP, extracting it, and running a .bat updater that swaps files
/// while the app is shut down.
/// </summary>
public sealed class GitHubUpdateService : IUpdateService
{
    private readonly HttpClient _http;
    private readonly ILogger<GitHubUpdateService> _logger;
    private GitHubRelease? _latestRelease;

    private const string RepoOwner = "Arjunyadavsoma";
    private const string RepoName = "PrintEXE";
    private const string PortableZipName = "PrintShopCompanion-portable.zip";

    public GitHubUpdateService(HttpClient http, ILogger<GitHubUpdateService> logger)
    {
        _http = http;
        _logger = logger;
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PrintShopCompanion", CurrentVersion));
    }

    public string CurrentVersion =>
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";

    public string? LatestVersion => _latestRelease?.TagName?.TrimStart('v');
    public bool UpdateAvailable => _latestRelease is not null && IsNewer(LatestVersion, CurrentVersion);
    public string? DownloadUrl => _latestRelease?.Assets?
        .FirstOrDefault(a => a.Name?.Equals(PortableZipName, StringComparison.OrdinalIgnoreCase) == true)?.BrowserDownloadUrl;

    public async Task<bool> CheckForUpdatesAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";
            var release = await _http.GetFromJsonAsync<GitHubRelease>(url, ct);
            if (release is null) return false;

            _latestRelease = release;
            var latest = release.TagName?.TrimStart('v') ?? "0.0.0.0";
            var isNewer = IsNewer(latest, CurrentVersion);

            _logger.LogInformation("Update check: current={Current}, latest={Latest}, updateAvailable={Available}",
                CurrentVersion, latest, isNewer);

            return isNewer;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to check for updates");
            return false;
        }
    }

    public async Task InstallUpdateAsync(IProgress<int>? progress = null, CancellationToken ct = default)
    {
        if (DownloadUrl is null)
            throw new InvalidOperationException("No update available. Call CheckForUpdatesAsync first.");

        // 1. Download the portable ZIP to %TEMP%.
        var tempDir = Path.Combine(Path.GetTempPath(), "PrintShop-update");
        if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        Directory.CreateDirectory(tempDir);

        var zipPath = Path.Combine(tempDir, PortableZipName);
        _logger.LogInformation("Downloading update from {Url}", DownloadUrl);
        progress?.Report(10);

        using (var resp = await _http.GetAsync(DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            resp.EnsureSuccessStatusCode();
            await using var fs = File.Create(zipPath);
            await resp.Content.CopyToAsync(fs, ct);
        }
        progress?.Report(50);

        // 2. Extract the ZIP.
        var extractDir = Path.Combine(tempDir, "new");
        Directory.CreateDirectory(extractDir);
        ZipFile.ExtractToDirectory(zipPath, extractDir, overwriteFiles: true);
        progress?.Report(70);

        // 3. Write the updater .bat script.
        var appDir = AppContext.BaseDirectory;
        var batPath = Path.Combine(tempDir, "updater.bat");
        var exeName = Process.GetCurrentProcess().ProcessName + ".exe";
        var exePath = Path.Combine(appDir, exeName);

        var batContent = "@echo off\r\n" +
            "echo Updating PrintShop Companion...\r\n" +
            "timeout /t 2 /nobreak >nul\r\n" +
            "echo Copying new files...\r\n" +
            $"xcopy \"{extractDir}\\*\" \"{appDir}\" /E /Y /I /Q\r\n" +
            "echo Starting updated app...\r\n" +
            $"start \"\" \"{exePath}\"\r\n" +
            "echo Cleaning up...\r\n" +
            $"rmdir /s /q \"{tempDir}\"\r\n";

        await File.WriteAllTextAsync(batPath, batContent, ct);
        progress?.Report(90);

        // 4. Launch the updater and exit.
        var psi = new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"{batPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = tempDir
        };
        Process.Start(psi);
        progress?.Report(100);

        _logger.LogInformation("Update installed. App will now exit for file swap.");
        // 5. Exit the app so the .bat can swap files.
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            System.Windows.Application.Current.Shutdown();
        });
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
}
