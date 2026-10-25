using System.Windows;
using System.Windows.Controls;

namespace RotaHub.Views;

public partial class LoginView : UserControl
{
    public LoginView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (string.IsNullOrEmpty(Email.Text))
                Email.Focus();
            else
                Password.Focus();
        };
    }
}
