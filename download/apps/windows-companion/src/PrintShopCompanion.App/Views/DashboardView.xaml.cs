namespace PrintShopCompanion.App.Views;

using System.Windows.Controls;

public partial class DashboardView : System.Windows.Controls.UserControl
{
    public DashboardView(ViewModels.DashboardViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
