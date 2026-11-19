namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Presentation;
using PrintShopCompanion.Core.Services;
using PrintShopCompanion.App.Services;
using PrintShopCompanion.Infrastructure.Backend;

/// <summary>
/// Overview page — a LIVE mirror of the shopkeeper webpage's dashboard.
/// Instead of fetching once when opened (and going stale), the VM binds to
/// the shared <see cref="ShopDataStore"/>: the 15s sync pull refreshes the
/// KPI cards and recent orders automatically, exactly when the webpage's
/// numbers change. KPI cards mirror the web dashboard's four headline cards
/// (Today's orders, Today's revenue, Pending payments, Total orders) plus
/// the print-pipeline cards (Printing, Ready).
/// </summary>
public partial class OverviewViewModel : ObservableObject
{
    private readonly IShopBackend _backend;
    private readonly ShopDataStore _store;
    private readonly INavigationService _nav;

    public OverviewViewModel(IShopBackend backend, ShopDataStore store, INavigationService nav)
    {
        _backend = backend;
        _store = store;
        _nav = nav;
        _store.PropertyChanged += OnStoreChanged;
    }

    /// <summary>Recent-orders row click → the order's detail page.</summary>
    public void OpenOrder(OrderRecord order) => _nav.NavigateTo("OrderDetails", order.Id);

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private ObservableCollection<OrderRecord> _recentOrders = new();

    // ── Live projections from the store ──
    public string ShopName => _store.ShopName;
    public string ShopNameInitial => _store.ShopNameInitial;
    public bool IsLive => _store.Online;
    public string LiveStatusText => _store.LiveStatusText;
    public string LastSyncText => _store.LastSyncText;

    public string RevenueFormatted =>
        _store.Analytics is null ? "—" : Rupees.Format(_store.Analytics.Revenue.TotalPaise);
    public int TotalOrders => _store.Analytics?.OrderCount.Total ?? 0;
    public int PaidOrders => _store.Analytics?.OrderCount.Paid ?? 0;
    public int Printing => _store.Analytics?.ByStatus.GetValueOrDefault(OrderStatus.Printing) ?? 0;
    public int Ready => _store.Analytics?.ByStatus.GetValueOrDefault(OrderStatus.Ready) ?? 0;

    /// <summary>Web dashboard parity: orders placed TODAY (local shop time).</summary>
    public int TodaysOrders => OrdersFromToday().Count;

    /// <summary>Web dashboard parity: revenue from orders paid TODAY.</summary>
    public string TodaysRevenueFormatted =>
        Rupees.Format(OrdersFromToday().Where(o => o.Status != OrderStatus.AwaitingPayment)
            .Sum(o => o.TotalAmountPaise));

    /// <summary>Web dashboard parity: orders still awaiting payment (payment queue).</summary>
    public int PendingPayments => _store.Orders.Count(o => o.Status == OrderStatus.AwaitingPayment);

    /// <summary>Explicit refresh — also publishes into the store so ALL pages update.</summary>
    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            // Wait a moment for session restore in background (unchanged behavior).
            if (!_backend.IsAuthenticated) await Task.Delay(2000);
            if (!_backend.IsAuthenticated)
            {
                ErrorMessage = "Please sign in to view your dashboard.";
                return;
            }

            if (_backend.CurrentShop is null)
            {
                try { await _backend.GetShopAsync(); } catch { }
            }

            var analytics = await _backend.GetAnalyticsAsync();
            var (orders, _) = await _backend.ListOrdersAsync(limit: 50);

            // One publish → Overview, Orders and Sync Center all refresh.
            _store.ApplyShopSnapshot(
                shopName: _backend.CurrentShop?.Name ?? "My Shop",
                analytics: analytics,
                orders: orders);
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

    private void OnStoreChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        switch (e.PropertyName)
        {
            case nameof(ShopDataStore.Orders):
                RecentOrders = new ObservableCollection<OrderRecord>(_store.Orders.Take(10));
                OnPropertyChanged(nameof(TodaysOrders));
                OnPropertyChanged(nameof(TodaysRevenueFormatted));
                OnPropertyChanged(nameof(PendingPayments));
                break;
            case nameof(ShopDataStore.Analytics):
                OnPropertyChanged(nameof(RevenueFormatted));
                OnPropertyChanged(nameof(TotalOrders));
                OnPropertyChanged(nameof(PaidOrders));
                OnPropertyChanged(nameof(Printing));
                OnPropertyChanged(nameof(Ready));
                break;
            case nameof(ShopDataStore.ShopName):
                OnPropertyChanged(nameof(ShopName));
                OnPropertyChanged(nameof(ShopNameInitial));
                break;
            case nameof(ShopDataStore.Online):
                OnPropertyChanged(nameof(IsLive));
                OnPropertyChanged(nameof(LiveStatusText));
                break;
            case nameof(ShopDataStore.LastSyncText):
                OnPropertyChanged(nameof(LastSyncText));
                break;
        }
    }

    private IReadOnlyList<OrderRecord> OrdersFromToday()
    {
        var today = DateTimeOffset.Now.Date;
        return _store.Orders.Where(o => o.CreatedAt.ToLocalTime().Date == today).ToList();
    }
}
