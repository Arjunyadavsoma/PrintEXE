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

    private async void Retry_Click(object sender, RoutedEventArgs e) => await _vm.RetryCommand.ExecuteAsync(null);
    private async void CancelJob_Click(object sender, RoutedEventArgs e) => await _vm.CancelCommand.ExecuteAsync(null);
    private async void ClearFailed_Click(object sender, RoutedEventArgs e) => await _vm.ClearFailedCommand.ExecuteAsync(null);
    private async void MarkResolved_Click(object sender, RoutedEventArgs e) => await _vm.MarkResolvedCommand.ExecuteAsync(null);
    private async void Reprint_Click(object sender, RoutedEventArgs e) => await _vm.ReprintCommand.ExecuteAsync(null);
}
