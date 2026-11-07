namespace PrintShopCompanion.App.Views;

using System.Windows;
using System.Windows.Controls;
using PrintShopCompanion.App.ViewModels;

public partial class LoginView : UserControl
{
    private readonly LoginViewModel _vm;

    public LoginView(LoginViewModel vm)
    {
        InitializeComponent();
        _vm = vm;
        DataContext = vm;
    }

    private void PasswordBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        _vm.Password = ((PasswordBox)sender).Password;
    }

    private async void SignIn_Click(object sender, RoutedEventArgs e)
    {
        await _vm.SignInAsync();
    }
}
