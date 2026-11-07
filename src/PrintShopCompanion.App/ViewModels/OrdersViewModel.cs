namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.App.Services;

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
    [ObservableProperty] private ObservableCollection<OrderRecord> _orders = new();

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
            var (orders, _) = await _backend.ListOrdersAsync(limit: 50);
            Orders = new ObservableCollection<OrderRecord>(orders);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Failed to load orders: {ex.Message}";
        }
        finally { IsLoading = false; }
    }
}
