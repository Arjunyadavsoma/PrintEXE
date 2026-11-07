namespace PrintShopCompanion.App.Views;

using System.Windows.Controls;
using PrintShopCompanion.App.ViewModels;

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
}
