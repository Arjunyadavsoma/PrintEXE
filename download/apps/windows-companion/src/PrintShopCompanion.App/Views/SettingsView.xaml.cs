namespace PrintShopCompanion.App.Views;

using System.Windows.Controls;

public partial class SettingsView : System.Windows.Controls.UserControl
{
    public SettingsView(ViewModels.SettingsViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
