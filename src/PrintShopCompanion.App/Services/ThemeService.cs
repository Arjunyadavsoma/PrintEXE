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

            // Swap ONLY the theme dictionary, matched by its Source. The old
            // implementation replaced MergedDictionaries[0] — which is
            // Tokens.xaml, the entire design system — nuking every token the
            // component styles reference. It also left Dark.xaml permanently
            // merged after Light.xaml, so duplicate keys resolved to the DARK
            // values even in light mode.
            var target = new Uri(
                $"pack://application:,,,/PrintShopCompanion.App;component/Themes/{(isDark ? "Dark" : "Light")}.xaml");

            var dicts = app.Resources.MergedDictionaries;
            for (var i = 0; i < dicts.Count; i++)
            {
                var src = dicts[i].Source;
                if (src is not null &&
                    (src.OriginalString.EndsWith("Light.xaml", StringComparison.OrdinalIgnoreCase)
                     || src.OriginalString.EndsWith("Dark.xaml", StringComparison.OrdinalIgnoreCase)))
                {
                    dicts[i] = new ResourceDictionary { Source = target };
                    return;
                }
            }

            // No theme dictionary found (unexpected) — append rather than
            // clobber the token dictionaries.
            dicts.Add(new ResourceDictionary { Source = target });
        }
        catch { }
    }
}
