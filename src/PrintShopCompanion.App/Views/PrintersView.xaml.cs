namespace PrintShopCompanion.App.Views;

using System.Windows;
using System.Windows.Controls;
using PrintShopCompanion.App.ViewModels;

public partial class PrintersView : UserControl
{
    private readonly PrintersViewModel _vm;
    public PrintersView(PrintersViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += (s, e) => vm.Refresh();
    }
    private void Refresh_Click(object sender, RoutedEventArgs e) => _vm.Refresh();
    private async void TestPrint_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.Selected != null)
        {
            await _vm.TestPrintAsync();
            StatusText.Text = _vm.TestStatus;
        }
    }
}
