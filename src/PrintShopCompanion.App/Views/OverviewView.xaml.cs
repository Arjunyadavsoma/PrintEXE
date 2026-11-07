namespace PrintShopCompanion.App.Views;

using System.Windows.Controls;
using PrintShopCompanion.App.ViewModels;

public partial class OverviewView : UserControl
{
    public OverviewView(OverviewViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.LoadCommand.Execute(null);
    }
}
