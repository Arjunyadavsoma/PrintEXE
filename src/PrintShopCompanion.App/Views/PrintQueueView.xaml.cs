namespace PrintShopCompanion.App.Views;

using System.Windows.Controls;

public partial class PrintQueueView : System.Windows.Controls.UserControl
{
    public PrintQueueView(ViewModels.PrintQueueViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
