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
    private readonly ThemeService _themeSvc;
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
    private string _renderer = "sumatra";

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
        _themeSvc = theme;
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
        _themeSvc.ApplyFromOptions();
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
    /// <summary>
    /// Persists a dotted configuration key to the per-user override file
    /// (%AppData%\PrintShop\Companion\appsettings.Local.json) via
    /// LocalSettingsStore. The App/Worker configuration chain loads that file
    /// AFTER appsettings.json, so the override wins on the next startup.
    /// <para><b>Phase 0 audit fix:</b> the previous implementation wrote the
    /// values to a throwaway Utf8JsonWriter and then literally overwrote the
    /// file with "{}" — every settings save silently DISCARDED all overrides.
    /// It also targeted the install directory (not writable under Program
    /// Files). Both bugs are fixed by delegating to LocalSettingsStore.</para>
    /// </summary>
    public static void Update(string key, string value)
    {
        PrintShopCompanion.Infrastructure.Storage.LocalSettingsStore.Set(key, value);
    }
}
