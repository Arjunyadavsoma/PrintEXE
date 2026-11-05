namespace PrintShopCompanion.App.ViewModels;

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;

public partial class SettingsViewModel : ObservableObject
{
    private readonly IOptionsMonitor<AppOptions> _appOpts;
    private readonly IOptionsMonitor<PrinterOptions> _printerOpts;
    private readonly AutoStartService _autoStart;
    private readonly ThemeService _theme;
    private readonly IPrintShopBackend _backend;

    [ObservableProperty]
    private bool _minimizeToTrayOnClose = true;

    [ObservableProperty]
    private bool _autoStartWithWindows = true;

    [ObservableProperty]
    private bool _followSystemTheme = true;

    [ObservableProperty]
    private string _theme = "system";

    [ObservableProperty]
    private string _renderer = "pdfium";

    [ObservableProperty]
    private string _selectedPrinter = "";

    [ObservableProperty]
    private string _shopName = "(not paired)";

    [ObservableProperty]
    private string _deviceId = "";

    [ObservableProperty]
    private string _appVersion = "0.1.0";

    public SettingsViewModel(
        IOptionsMonitor<AppOptions> appOpts,
        IOptionsMonitor<PrinterOptions> printerOpts,
        AutoStartService autoStart,
        ThemeService theme,
        IPrintShopBackend backend)
    {
        _appOpts = appOpts;
        _printerOpts = printerOpts;
        _autoStart = autoStart;
        _theme = theme;
        _backend = backend;
        Load();
    }

    private void Load()
    {
        MinimizeToTrayOnClose = _appOpts.CurrentValue.MinimizeToTrayOnClose;
        AutoStartWithWindows  = _appOpts.CurrentValue.AutoStartWithWindows;
        FollowSystemTheme     = _appOpts.CurrentValue.FollowSystemTheme;
        Theme                 = _appOpts.CurrentValue.Theme;
        Renderer              = _printerOpts.CurrentValue.Renderer;
        SelectedPrinter      = _printerOpts.CurrentValue.SelectedPrinter ?? "(Windows default)";
        ShopName              = _backend.CurrentShop?.ShopName ?? "(not paired)";
        DeviceId              = _backend.CurrentDevice?.DeviceId ?? "(not paired)";
        AppVersion            = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString() ?? "0.1.0";
    }

    [RelayCommand]
    private void Save()
    {
        // Persist to appsettings.json via a tiny OptionsWriter (see Services/OptionsWriter.cs).
        OptionsWriter.Update("App:MinimizeToTrayOnClose", MinimizeToTrayOnClose.ToString().ToLowerInvariant());
        OptionsWriter.Update("App:AutoStartWithWindows", AutoStartWithWindows.ToString().ToLowerInvariant());
        OptionsWriter.Update("App:FollowSystemTheme", FollowSystemTheme.ToString().ToLowerInvariant());
        OptionsWriter.Update("App:Theme", Theme);
        OptionsWriter.Update("Printer:Renderer", Renderer);
        _autoStart.SyncFromOptions();
        _theme.ApplyFromOptions();
    }

    [RelayCommand]
    private async Task RevokeDeviceAsync()
    {
        await _backend.RevokeDeviceAsync();
        ShopName = "(not paired)";
        DeviceId = "(not paired)";
    }

    [RelayCommand]
    private void OpenLogDir()
    {
        var dir = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "PrintShop", "Companion", "logs");
        if (!System.IO.Directory.Exists(dir)) System.IO.Directory.CreateDirectory(dir);
        System.Diagnostics.Process.Start("explorer.exe", dir);
    }
}

internal static class OptionsWriter
{
    public static void Update(string key, string value)
    {
        // Writes appsettings.Local.json with the override. The next startup picks it up.
        var path = System.IO.Path.Combine(AppContext.BaseDirectory, "appsettings.Local.json");
        var json = System.IO.File.Exists(path) ? System.IO.File.ReadAllText(path) : "{}";
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        using var ms = new System.IO.MemoryStream();
        using (var writer = new System.Text.Json.Utf8JsonWriter(ms, new System.Text.Json.JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                prop.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        System.IO.File.WriteAllText(path, "{}");
    }
}
