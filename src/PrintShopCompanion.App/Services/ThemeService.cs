namespace PrintShopCompanion.App.Services;

using System.Windows;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Options;

public sealed class ThemeService
{
    private readonly IOptionsMonitor<AppOptions> _options;

    public ThemeService(IOptionsMonitor<AppOptions> options) => _options = options;

    public void ApplyFromOptions()
    {
        var app = Application.Current;
        if (app == null) return;
        try
        {
            var theme = _options.CurrentValue.FollowSystemTheme ? "system" : _options.CurrentValue.Theme;
            var isDark = theme == "dark";
            var dict = new ResourceDictionary
            {
                Source = new Uri($"Themes/{(isDark ? "Dark" : "Light")}.xaml", UriKind.Relative)
            };
            if (app.Resources.MergedDictionaries.Count > 0)
                app.Resources.MergedDictionaries[0] = dict;
            else
                app.Resources.MergedDictionaries.Add(dict);
        }
        catch { }
    }
}
