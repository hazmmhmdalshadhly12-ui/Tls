using System.Windows;
using System.Windows.Controls;
using HAZEM.Tunnel.App.ViewModels;

namespace HAZEM.Tunnel.App.Views;

public partial class ServersView : UserControl
{
    public ServersView()
    {
        InitializeComponent();
        TestPasswordBox.PasswordChanged += (_, _) =>
        {
            if (DataContext is ServersViewModel vm)
                vm.TestPassword = TestPasswordBox.Password;
        };
    }
}
