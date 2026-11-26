namespace PrintShopCompanion.App.Views;

using System.Windows;
using System.Windows.Controls;
using PrintShopCompanion.App.ViewModels;
using PrintShopCompanion.Infrastructure.Printers;

public partial class PrintersView : UserControl
{
    private readonly PrintersViewModel _vm;
    private SpoolerChangeWatcher? _watcher;

    public PrintersView(PrintersViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _vm.Refresh();

        // v0.7.17: spooler change notifications keep the list current without
        // a manual refresh (technique adapted from QZ Tray's connections
        // watchdog — docs/research/OPEN_SOURCE_FEATURE_EVIDENCE_MATRIX.md A5).
        // Constructed without a logger: the view layer owns it transiently and
        // diagnostics of watcher failures land in the spooler surface itself.
        if (_watcher is null)
        {
            _watcher = new SpoolerChangeWatcher();
            _watcher.PrintersChanged += _ => Dispatcher.BeginInvoke(() =>
            {
                // Refresh on the UI thread; refresh preserves selection.
                _vm.RefreshFromSpoolerChange();
            });
            _watcher.Start();
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        // Stop the watcher when leaving the page (background thread + OS
        // handle should not outlive the view); re-created on next load.
        _watcher?.Dispose();
        _watcher = null;
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
