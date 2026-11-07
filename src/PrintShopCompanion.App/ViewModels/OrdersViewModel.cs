namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.App.Services;

/// <summary>
/// The Orders list screen. Filterable by status. Backed by
/// GET /shops/me/orders?status=&amp;limit=&amp;offset=.
/// </summary>
public partial class OrdersViewModel : ObservableObject
{
    private readonly IShopBackend _backend;
    private readonly INavigationService _nav;

    public OrdersViewModel(IShopBackend backend, INavigationService nav)
    {
        _backend = backend;
        _nav = nav;
    }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private string? _searchTerm;
    [ObservableProperty] private OrderStatus? _statusFilter;
    [ObservableProperty] private ObservableCollection<OrderRecord> _orders = new();
    [ObservableProperty] private OrderRecord? _selected;

    [RelayCommand]
    public async Task LoadAsync()
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var (orders, _) = await _backend.ListOrdersAsync(StatusFilter, limit: 50);
            Orders = new ObservableCollection<OrderRecord>(orders);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public void OpenOrder(OrderRecord order)
    {
        _nav.NavigateToOrderDetails(order.Id);
    }

    partial void OnStatusFilterChanged(OrderStatus? value) => _ = LoadAsync();
}
