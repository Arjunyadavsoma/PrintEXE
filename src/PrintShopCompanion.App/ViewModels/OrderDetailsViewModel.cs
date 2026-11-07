namespace PrintShopCompanion.App.ViewModels;

using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PrintShopCompanion.Core.Contracts;
using PrintShopCompanion.Core.Models;
using PrintShopCompanion.App.Services;

/// <summary>
/// The Order Details screen. Shows order fields, files, payment info, and
/// exposes the order state-machine transitions (Start, Complete, Cancel).
/// Backed by GET /shops/me/orders/{id} + POST /shops/me/orders/{id}/{start,complete,cancel}.
/// </summary>
public partial class OrderDetailsViewModel : ObservableObject
{
    private readonly IShopBackend _backend;
    private readonly INavigationService _nav;

    public OrderDetailsViewModel(IShopBackend backend, INavigationService nav)
    {
        _backend = backend;
        _nav = nav;
    }

    [ObservableProperty] private bool _isLoading;
    [ObservableProperty] private string? _errorMessage;
    [ObservableProperty] private OrderRecord? _order;
    [ObservableProperty] private ObservableCollection<OrderFile> _files = new();
    [ObservableProperty] private string _notes = "";

    // Status flags driving which action buttons are enabled.
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private bool _canComplete;
    [ObservableProperty] private bool _canCancel;

    public string? OrderId => null;

    [RelayCommand]
    public async Task LoadAsync()
    {
        if (OrderId is null) { _nav.NavigateTo("Orders"); return; }
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            Order = await _backend.GetOrderAsync(OrderId);
            Files = new ObservableCollection<OrderFile>(Order.Files ?? Array.Empty<OrderFile>());
            Notes = Order.Notes ?? "";
            CanStart    = Order.Status.CanTransitionTo(OrderStatus.Printing);
            CanComplete = Order.Status.CanTransitionTo(OrderStatus.Collected);
            CanCancel   = Order.Status.CanTransitionTo(OrderStatus.Cancelled);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public async Task StartAsync()
    {
        if (Order is null) return;
        IsLoading = true;
        try
        {
            await _backend.StartOrderAsync(Order.Id);
            await LoadAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public async Task CompleteAsync()
    {
        if (Order is null) return;
        IsLoading = true;
        try
        {
            await _backend.CompleteOrderAsync(Order.Id);
            await LoadAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public async Task CancelAsync()
    {
        if (Order is null) return;
        IsLoading = true;
        try
        {
            await _backend.CancelOrderAsync(Order.Id);
            await LoadAsync();
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
        finally { IsLoading = false; }
    }

    [RelayCommand]
    public async Task SaveNotesAsync()
    {
        if (Order is null) return;
        try { await _backend.UpdateOrderNotesAsync(Order.Id, Notes); }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public async Task DownloadFileAsync(OrderFile? file)
    {
        if (Order is null || file is null) return;
        try
        {
            using var stream = await _backend.DownloadFileAsync(Order.Id, file.Id);
            // Save to the user's Downloads folder.
            var downloadsDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Downloads");
            Directory.CreateDirectory(downloadsDir);
            var savePath = Path.Combine(downloadsDir, file.OriginalFilename);
            await using var fs = File.Create(savePath);
            await stream.CopyToAsync(fs);
        }
        catch (Exception ex) { ErrorMessage = ex.Message; }
    }

    [RelayCommand]
    public void Back() => _nav.NavigateTo("Orders");
}
