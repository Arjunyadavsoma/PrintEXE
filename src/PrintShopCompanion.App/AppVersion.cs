namespace PrintShopCompanion.App;

using System.Reflection;

/// <summary>
/// Central version source for the companion app. Reads the assembly version
/// (which Directory.Build.props sets from BUILD_VERSION at build time) and
/// exposes it as a clean "X.Y.Z" short form for display in the UI and logs.
///
/// Never hardcode a version string anywhere else — every label, log line and
/// update-check must read from here so the auto-updater can actually move the
/// version forward after a successful file swap.
/// </summary>
public static class AppVersion
{
    /// <summary>
    /// Full four-part assembly version, e.g. "0.4.3.0".
    /// </summary>
    public static string Full
    {
        get
        {
            try
            {
                var asm = Assembly.GetEntryAssembly()
                          ?? Assembly.GetExecutingAssembly();
                return asm.GetName().Version?.ToString() ?? "0.0.0.0";
            }
            catch { return "0.0.0.0"; }
        }
    }

    /// <summary>
    /// Three-part "X.Y.Z" form for display, e.g. "0.4.3".
    /// Falls back to "0.0.0" if the assembly version can't be parsed.
    /// </summary>
    public static string Short
    {
        get
        {
            try
            {
                var v = Version.Parse(Full);
                return $"{v.Major}.{v.Minor}.{v.Build}";
            }
            catch { return "0.0.0"; }
        }
    }

    /// <summary>
    /// Prefixed "vX.Y.Z" form for display in the sidebar / settings, e.g. "v0.4.3".
    /// </summary>
    public static string Prefixed => $"v{Short}";
}
