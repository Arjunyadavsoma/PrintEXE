namespace PrintShopCompanion.App.Views;

using System.Windows.Controls;
using System.Windows.Input;
using PrintShopCompanion.App.ViewModels;
using PrintShopCompanion.Core.Models;

public partial class OverviewView : UserControl
{
    private readonly OverviewViewModel _vm;

    public OverviewView(OverviewViewModel vm)
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

    /// <summary>Recent-orders row click → the order's detail page.</summary>
    private void Row_Click(object sender, MouseButtonEventArgs e)
    {
        if ((sender as System.Windows.FrameworkElement)?.DataContext is OrderRecord order)
            _vm.OpenOrder(order);
    }
}
