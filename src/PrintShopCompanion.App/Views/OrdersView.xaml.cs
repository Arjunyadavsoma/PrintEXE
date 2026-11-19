namespace PrintShopCompanion.App.Views;

using System.Windows.Controls;
using System.Windows.Input;
using PrintShopCompanion.App.ViewModels;
using PrintShopCompanion.Core.Models;

public partial class OrdersView : UserControl
{
    private readonly OrdersViewModel _vm;
    public OrdersView(OrdersViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (s, e) => await vm.LoadAsync();
    }
    private async void Refresh_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        await _vm.LoadAsync();
    }

    /// <summary>Functional filter chips — actually filter the list.</summary>
    private void Filter_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.Tag is string filter)
            _vm.SetFilterCommand.Execute(filter);
    }

    /// <summary>Row click → Order Details page for that order.</summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.DataContext is OrderRecord order)
            _vm.OpenOrderCommand.Execute(order);
    }
}
