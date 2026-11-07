namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;

/// <summary>
/// The Overview screen — the shopkeeper's main dashboard. Shows the analytics
/// cards (revenue, order counts by status, popular settings) and the recent
/// orders list. Backed by GET /shops/me/analytics + GET /shops/me/orders?limit=10.
/// </summary>
public partial class OverviewViewModel : ObservableObject
{
    private readonly IShopBackend _backend;

    public OverviewViewModel(IShopBackend backend) => _backend = backend;

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string _shopName = "";
    [ObservableProperty] private string _revenueFormatted = "—";
    [ObservableProperty] private int _totalOrders;
    [ObservableProperty] private int _paidOrders;
    [ObservableProperty] private int _awaitingPayment;
    [ObservableProperty] private int _printing;
    [ObservableProperty] private int _ready;
    [ObservableProperty] private int _collected;
    [ObservableProperty] private int _cancelled;
    [ObservableProperty] private int _popularColor;
    [ObservableProperty] private int _popularBw;
    [ObservableProperty] private int _popularDuplex;
    [ObservableProperty] private int _popularSimplex;
    [ObservableProperty] private ObservableCollection<OrderRecord> _recentOrders = new();

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            if (_backend.CurrentShop is null)
                await _backend.GetShopAsync();
            ShopName = _backend.CurrentShop?.Name ?? "";

            var analytics = await _backend.GetAnalyticsAsync();
            RevenueFormatted = analytics.Revenue.Formatted;
            TotalOrders = analytics.OrderCount.Total;
            PaidOrders = analytics.OrderCount.Paid;
            AwaitingPayment = analytics.ByStatus.GetValueOrDefault(OrderStatus.AwaitingPayment);
            Printing = analytics.ByStatus.GetValueOrDefault(OrderStatus.Printing);
            Ready = analytics.ByStatus.GetValueOrDefault(OrderStatus.Ready);
            Collected = analytics.ByStatus.GetValueOrDefault(OrderStatus.Collected);
            Cancelled = analytics.ByStatus.GetValueOrDefault(OrderStatus.Cancelled);
            PopularColor = analytics.Popular.Color;
            PopularBw = analytics.Popular.Bw;
            PopularDuplex = analytics.Popular.Duplex;
            PopularSimplex = analytics.Popular.Simplex;

            var (orders, _) = await _backend.ListOrdersAsync(limit: 10);
            RecentOrders = new ObservableCollection<OrderRecord>(orders);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally { IsLoading = false; }
    }
}
