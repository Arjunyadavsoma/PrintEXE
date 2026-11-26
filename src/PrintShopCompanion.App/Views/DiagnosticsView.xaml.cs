namespace PrintShopCompanion.App.Views;

using System.Windows.Controls;
using Microsoft.Extensions.DependencyInjection;

public partial class DiagnosticsView : UserControl
{
    public DiagnosticsView(IServiceProvider services)
    {
        InitializeComponent();
        // Match the other screens: the view resolves its own VM from DI.
        DataContext = services.GetRequiredService<PrintShopCompanion.App.ViewModels.DiagnosticsViewModel>();
    }
}
