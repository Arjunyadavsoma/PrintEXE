namespace PrintShopCompanion.App.Services;

using System.Diagnostics;
using System.IO;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Options;

/// <summary>
/// Manages the per-user "Run on Windows startup" shortcut in the
/// <c>%AppData%\Microsoft\Windows\Start Menu\Programs\Startup</c> folder.
/// No admin elevation is required — this is a per-user shortcut, not a
/// per-machine Run-key change.
/// </summary>
public sealed class AutoStartService
{
    private readonly IOptionsMonitor<AppOptions> _options;

    public AutoStartService(IOptionsMonitor<AppOptions> options) => _options = options;

    public void SyncFromOptions()
    {
        try
        {
            var startupDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Microsoft", "Windows", "Start Menu", "Programs", "Startup");
            Directory.CreateDirectory(startupDir);
            var shortcutPath = Path.Combine(startupDir, "PrintShop Companion.lnk");

            if (_options.CurrentValue.AutoStartWithWindows)
            {
                CreateShortcut(shortcutPath, Environment.ProcessPath!, "auto");
            }
            else
            {
                if (File.Exists(shortcutPath)) File.Delete(shortcutPath);
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"AutoStart sync failed: {ex.Message}");
        }
    }

    private static void CreateShortcut(string path, string target, string args)
    {
        // Uses the Windows Script Host (WSH) via ProcessStart to create the .lnk.
        // Production build uses IWshRuntimeLibrary directly via COM; for the first
        // delivery we shell out to PowerShell so we don't need the interop assembly.
        var script = $"""
            $s = (New-Object -ComObject WScript.Shell).CreateShortcut("{path}")
            $s.TargetPath = "{target}"
            $s.Arguments = "{args}"
            $s.WorkingDirectory = "{Path.GetDirectoryName(target)}"
            $s.WindowStyle = 7
            $s.Description = "PrintShop Companion"
            $s.Save()
            """;
        var psi = new ProcessStartInfo("powershell.exe", $"-NoProfile -Command \"{script.Replace("\"", "`\"")}\"")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        Process.Start(psi)?.WaitForExit(5000);
    }
}
