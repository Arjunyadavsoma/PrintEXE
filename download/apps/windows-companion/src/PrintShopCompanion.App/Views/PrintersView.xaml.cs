namespace PrintShopCompanion.App.Views;

using System.Windows.Controls;

public partial class PrintersView : System.Windows.Controls.UserControl
{
    public PrintersView(ViewModels.PrintersViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
