namespace PrintShopCompanion.App.Views;

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PrintShopCompanion.App.ViewModels;
using PrintShopCompanion.Core.Models;

public partial class OrderDetailsView : UserControl
{
    private OrderDetailsViewModel? _vm;

    public OrderDetailsView()
    {
        InitializeComponent();
    }

    /// <summary>
    /// MainWindow parameterises the VM (order id) BEFORE showing this view, so
    /// it hands the exact VM instance across here. Loaded then triggers Load.
    /// </summary>
    public void AttachViewModel(OrderDetailsViewModel vm)
    {
        _vm = vm;
        DataContext = vm;
        vm.PropertyChanged += OnVmPropertyChanged;
        ApplyStatusPill();
        Loaded += async (s, e) => await vm.LoadAsync();
    }

    private void OnVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(OrderDetailsViewModel.StatusStyle) or "Order")
            ApplyStatusPill();
    }

    /// <summary>Color the header status pill with the web's semantic status colors.</summary>
    private void ApplyStatusPill()
    {
        var style = _vm?.StatusStyle;
        if (style is null || _vm?.Order is null)
        {
            StatusPill.Visibility = Visibility.Collapsed;
            return;
        }
        StatusPill.Visibility = Visibility.Visible;
        StatusPill.Background = (Brush)FindResource("SurfaceBrush");
        StatusPill.Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Background));
        StatusPill.BorderBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Border));
        StatusPill.BorderThickness = new Thickness(1);
        StatusPillText.Text = style.Label;
        StatusPillText.Foreground = new SolidColorBrush((Color)ColorConverter.ConvertFromString(style.Foreground));
    }

    private void Back_Click(object sender, RoutedEventArgs e) =>
        _vm?.Back();

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        await _vm.LoadAsync();
    }

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (MessageBox.Show("Move this order to PRINTING?", "Start printing",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _vm.StartAsync();
    }

    private async void Complete_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (MessageBox.Show("Mark this order as COLLECTED / completed?", "Complete order",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await _vm.CompleteAsync();
    }

    private async void Cancel_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if (MessageBox.Show("CANCEL this order? This cannot be undone.",
                "Cancel order", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        await _vm.CancelAsync();
    }

    private async void SaveNotes_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        SaveNotesButton.IsEnabled = false;
        SaveNotesButton.Content = "Saving…";
        try
        {
            await _vm.SaveNotesAsync();
            SaveNotesButton.Content = "Saved ✓";
        }
        finally
        {
            SaveNotesButton.IsEnabled = true;
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            t.Tick += (s, _) => { SaveNotesButton.Content = "Save notes"; ((System.Windows.Threading.DispatcherTimer)s).Stop(); };
            t.Start();
        }
    }

    private async void DownloadFile_Click(object sender, RoutedEventArgs e)
    {
        if (_vm is null) return;
        if ((sender as FrameworkElement)?.DataContext is not OrderFile file) return;
        await _vm.DownloadFileAsync(file);
    }
}
