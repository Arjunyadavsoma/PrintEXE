namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.Options;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Options;

public partial class MainViewModel : ObservableObject
{
    public string AppName => "PrintShop Companion";
    public string Version => typeof(MainViewModel).Assembly.GetName().Version?.ToString() ?? "0.1.0";

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string? _shopName;

    [ObservableProperty]
    private int _pendingCount;

    [ObservableProperty]
    private int _printingCount;

    [ObservableProperty]
    private int _completedToday;

    [ObservableProperty]
    private int _failedCount;
}
