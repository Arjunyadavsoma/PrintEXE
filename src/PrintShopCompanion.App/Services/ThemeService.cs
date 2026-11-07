namespace PrintShopCompanion.App.Services;

using System.Windows;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Options;

/// <summary>
/// Applies the chosen theme (light / dark / system) and reacts to
/// Windows theme changes. The Fluent theme is provided by WPF-UI;
/// this service flips the resource dictionary on the app's Resources
/// to switch between Light and Dark variants.
/// </summary>
public sealed class ThemeService
{
    private readonly IOptionsMonitor<AppOptions> _options;
    private bool _isDark;

    public ThemeService(IOptionsMonitor<AppOptions> options) => _options = options;

    public bool IsDark => _isDark;

    public void ApplyFromOptions()
    {
        var o = _options.CurrentValue;
        var isDark = o.Theme.ToLowerInvariant() switch
        {
            "dark" => true,
            "light" => false,
            _ => IsSystemDark()
        };
        ApplyTheme(isDark);

        // Hook Windows theme change events.
        SystemParameters.StaticPropertyChanged += (_, _) =>
        {
            if (_options.CurrentValue.FollowSystemTheme) ApplyTheme(IsSystemDark());
        };
    }

    public void ApplyTheme(bool dark)
    {
        _isDark = dark;
        var dict = new ResourceDictionary
        {
            Source = new Uri($"Themes/{(dark ? "Dark" : "Light")}.xaml", UriKind.Relative)
        };
        // Replace the first merged dictionary (the per-theme colors file).
        var app = Application.Current;
        if (app.Resources.MergedDictionaries.Count > 0)
            app.Resources.MergedDictionaries[0] = dict;
        else
            app.Resources.MergedDictionaries.Add(dict);
    }

    private static bool IsSystemDark()
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
        if (key?.GetValue("AppsUseLightTheme") is int v) return v == 0;
        return false;
    }
}
