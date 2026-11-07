namespace PrintShopCompanion.App.Views;

using System.Windows;
using System.Windows.Controls;
using PrintShopCompanion.App.ViewModels;

public partial class LoginView : UserControl
{
    public LoginView(LoginViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is LoginViewModel vm)
            vm.Password = ((PasswordBox)sender).Password;
    }
}
