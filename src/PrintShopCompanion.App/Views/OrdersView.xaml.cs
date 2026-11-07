namespace PrintShopCompanion.App.Views;

using System.Windows.Controls;
using PrintShopCompanion.App.ViewModels;

public partial class OrdersView : UserControl
{
    public OrdersView(OrdersViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        vm.LoadCommand.Execute(null);
    }
}
