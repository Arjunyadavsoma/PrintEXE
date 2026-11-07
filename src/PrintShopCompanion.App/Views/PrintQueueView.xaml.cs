namespace PrintShopCompanion.App.Views;

using System.Windows;
using System.Windows.Controls;
using PrintShopCompanion.App.ViewModels;

public partial class PrintQueueView : UserControl
{
    private readonly PrintQueueViewModel _vm;
    public PrintQueueView(PrintQueueViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (s, e) => vm.Refresh();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => _vm.Refresh();
}
