namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.Core.Services;
using PrintShopCompanion.App.Services;

/// <summary>
/// Orders page. Live: the list binds to the shared <see cref="ShopDataStore"/>
/// (fed by the 15s sync pull), so new orders appear WITHOUT a manual refresh —
/// exactly when they appear on the shopkeeper webpage. A status filter
/// (functional, not decorative) and row click-through to OrderDetails are wired.
/// </summary>
public partial class OrdersViewModel : ObservableObject
{
    private readonly IShopBackend _backend;
    private readonly INavigationService _nav;
    private readonly ShopDataStore _store;

    public OrdersViewModel(IShopBackend backend, INavigationService nav, ShopDataStore store)
    {
        _backend = backend;
        _nav = nav;
        _store = store;
        _store.PropertyChanged += OnStoreChanged;
        ApplyFilter();
    }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;

    /// <summary>Visible (filtered) orders — what the list binds to.</summary>
    public ObservableCollection<OrderRecord> Orders { get; } = new();

    /// <summary>Shop name for the header (updates live with the store).</summary>
    public string ShopName => _store.ShopName;

    /// <summary>Counts by filter for the header chips.</summary>
    public int AllCount => _store.Orders.Count;

    /// <summary>Filter values matching the webpage's order filters.</summary>
    public static readonly string[] FilterOptions = { "All", "Awaiting payment", "Paid", "Printing", "Ready", "Completed" };

    [ObservableProperty] private string _selectedFilter = "All";

    partial void OnSelectedFilterChanged(string value) => ApplyFilter();

    /// <summary>Live: the sync tick replaced the store snapshot → re-filter.</summary>
    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShopDataStore.Orders) or nameof(ShopDataStore.ShopName))
        {
            if (e.PropertyName == nameof(ShopDataStore.ShopName)) OnPropertyChanged(nameof(ShopName));
            ApplyFilter();
        }
    }

    private void ApplyFilter()
    {
        var snapshot = _store.Orders;
        Orders.Clear();
        foreach (var o in snapshot)
        {
            var keep = SelectedFilter switch
            {
                "Awaiting payment" => o.Status == OrderStatus.AwaitingPayment,
                "Paid"             => o.Status == OrderStatus.Paid,
                "Printing"         => o.Status == OrderStatus.Printing,
                "Ready"            => o.Status == OrderStatus.Ready,
                "Completed"        => o.Status == OrderStatus.Collected,
                _                  => true,
            };
            if (keep) Orders.Add(o);
        }
        OnPropertyChanged(nameof(AllCount));
    }

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (IsLoading) return;
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            if (!_backend.IsAuthenticated)
            {
                ErrorMessage = "Please sign in to view orders.";
                return;
            }
            // Immediate pull so the page is fresh on open (the store also
            // refreshes it every 15s via the sync engine).
            var (orders, _) = await _backend.ListOrdersAsync(limit: 50);
            _store.ApplyShopSnapshot(shopName: null, analytics: null, orders: orders);
            ApplyFilter();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load orders: {ex.Message}";
        }
        finally { IsLoading = false; }
    }

    /// <summary>Row click → the order's detail page (was missing entirely).</summary>
    [RelayCommand]
    private void OpenOrder(OrderRecord? order)
    {
        if (order is null) return;
        _nav.NavigateTo("OrderDetails", order.Id);
    }

    [RelayCommand]
    private void SetFilter(string filter) => SelectedFilter = filter;
}
