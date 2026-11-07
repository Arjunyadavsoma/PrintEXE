namespace PrintShopCompanion.App.Views;

using System.Windows.Controls;

public partial class SetupView : System.Windows.Controls.UserControl
{
    public SetupView(PrintShopCompanion.App.ViewModels.SetupViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
