namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Infrastructure.Backend;

public partial class OverviewViewModel : ObservableObject
{
    private readonly IShopBackend _backend;

    public OverviewViewModel(IShopBackend backend) => _backend = backend;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _shopName = "Loading...";
    [ObservableProperty] private string _revenueFormatted = "—";
    [ObservableProperty] private int _totalOrders;
    [ObservableProperty] private int _paidOrders;
    [ObservableProperty] private int _printing;
    [ObservableProperty] private int _ready;
    [ObservableProperty] private ObservableCollection<OrderRecord> _recentOrders = new();

    /// <summary>
    /// First letter of the shop name, used as the avatar fallback in the
    /// overview banner. Returns "P" while the shop name is still loading.
    /// </summary>
    public string ShopNameInitial => string.IsNullOrWhiteSpace(ShopName) || ShopName == "Loading..."
        ? "P"
        : ShopName.Substring(0, 1).ToUpperInvariant();

    partial void OnShopNameChanged(string value) => OnPropertyChanged(nameof(ShopNameInitial));

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading) return; // Prevent double-load
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            // Wait a moment for session to be restored in background
            if (!_backend.IsAuthenticated)
            {
                await Task.Delay(2000);
            }

            if (!_backend.IsAuthenticated)
            {
                ErrorMessage = "Please sign in to view your dashboard.";
                return;
            }

            if (_backend.CurrentShop is null)
            {
                try { await _backend.GetShopAsync(); } catch { }
            }
            ShopName = _backend.CurrentShop?.Name ?? "My Shop";

            var analytics = await _backend.GetAnalyticsAsync();
            RevenueFormatted = analytics.Revenue.Formatted;
            TotalOrders = analytics.OrderCount.Total;
            PaidOrders = analytics.OrderCount.Paid;
            Printing = analytics.ByStatus.GetValueOrDefault(OrderStatus.Printing);
            Ready = analytics.ByStatus.GetValueOrDefault(OrderStatus.Ready);

            var (orders, _) = await _backend.ListOrdersAsync(limit: 10);
            RecentOrders = new ObservableCollection<OrderRecord>(orders);
        }
        catch (PrintShopApiException ex)
        {
            ErrorMessage = ex.ErrorCode == "UNAUTHORIZED"
                ? "Your session has expired. Please sign in again."
                : $"Failed to load dashboard: {ex.Message}";
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Connection error: {ex.Message}";
        }
        finally { IsLoading = false; }
    }
}
