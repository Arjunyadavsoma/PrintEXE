namespace PrintShopCompanion.Infrastructure.Diagnostics;

using System.Diagnostics;
using System.Runtime.InteropServices;

/// <summary>
/// Lightweight Windows-specific machine diagnostics surfaced to the UI
/// ("this PC is called {hostname}, on Windows 11, has {X} GB RAM") and to the
/// backend heartbeat (so the shop admin can see at a glance what version of
/// Windows each paired device is running).
/// </summary>
public static class SystemDiagnostics
{
    public static string Hostname => Environment.MachineName;
    public static string OperatingSystem => Environment.OSVersion.VersionString;
    public static string CpuArchitecture => RuntimeInformation.OSArchitecture.ToString();
    public static string DotNetRuntime => RuntimeInformation.FrameworkDescription;

    public static (bool IsWindows11, Version Version) DetectWindowsVersion()
    {
        var v = Environment.OSVersion.Version;
        return (v.Major >= 10 && v.Build >= 22000, v);
    }

    public static bool IsRunningAsAdministrator()
    {
        using var id = Process.GetCurrentProcess();
        try
        {
            // Reach into WindowsIdentity for an authoritative check.
            var wi = new System.Security.Principal.WindowsIdentity(id.Id);
            return new System.Security.Principal.WindowsPrincipal(wi)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    public static long AvailableDiskBytes()
    {
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData))!);
            return drive.AvailableFreeSpace;
        }
        catch { return -1; }
    }

    public static string CompanionDataDir()
    {
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrintShop", "Companion");
    }

    public static string CompanionLogDir() => Path.Combine(CompanionDataDir(), "logs");
    public static string CompanionPdfCacheDir() => Path.Combine(Path.GetTempPath(), "PrintShop", "Companion", "pdfs");
    public static string CompanionQueueDbPath() => Path.Combine(CompanionDataDir(), "queue.db");
}
