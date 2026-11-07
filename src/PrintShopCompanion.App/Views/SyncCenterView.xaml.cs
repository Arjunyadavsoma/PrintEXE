namespace PrintShopCompanion.App.Views;

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using PrintShopCompanion.App.ViewModels;

public partial class SyncCenterView : UserControl
{
    private readonly SyncCenterViewModel _vm;

    public SyncCenterView(SyncCenterViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += async (s, e) => await vm.LoadAsync();
    }

    private async void SyncNow_Click(object sender, RoutedEventArgs e)
    {
        ManualSyncButton.IsEnabled = false;
        ManualSyncButton.Content = "Syncing...";
        try
        {
            await _vm.SyncNowAsync();
        }
        finally
        {
            ManualSyncButton.IsEnabled = true;
            ManualSyncButton.Content = "↻  Sync Now";
        }
    }
}
