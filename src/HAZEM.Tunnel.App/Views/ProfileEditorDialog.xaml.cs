using System.Windows;
using System.Windows.Controls;
using HAZEM.Tunnel.App.ViewModels;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Validation;
using Microsoft.Extensions.DependencyInjection;

namespace HAZEM.Tunnel.App.Views;

/// <summary>
/// Transport-aware profile editor. Secrets typed here go straight into the OS credential
/// store (DPAPI) and are wiped from the text boxes immediately after save.
/// </summary>
public partial class ProfileEditorDialog : Window
{
    public ProfileEditorViewModel ViewModel { get; }

    public ProfileEditorDialog(HAZEM.Tunnel.Core.Models.TunnelProfile editing, bool isNew)
    {
        InitializeComponent();
        ViewModel = new ProfileEditorViewModel(App.Services.GetRequiredService<HAZEM.Tunnel.Core.Interfaces.ICredentialStore>())
        {
            Editing = editing,
            IsNew = isNew
        };
        DataContext = ViewModel;
        Loaded += (_, _) =>
        {
            ViewModel.LoadFromProfile();
            ViewModel.ValidateNowCommand.Execute(null);
        };
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        // Persist typed secrets first (if any).
        var vm = ViewModel;
        var candidate = vm.Build();
        if (candidate is null) return;

        if (vm.ShowSshPassword && SshPasswordBox.Password.Length > 0)
        {
            vm.SetSshPassword(SshPasswordBox.Password);
            SshPasswordBox.Clear();
        }
        if (vm.ShowSshKey && SshKeyBox.Text.Trim().Length > 0)
        {
            vm.SetSshPrivateKey(SshKeyBox.Text);
            SshKeyBox.Clear();
        }
        if (vm.ShowSshKey && SshPassphraseBox.Password.Length > 0)
        {
            vm.SetSshPassphrase(SshPassphraseBox.Password);
            SshPassphraseBox.Clear();
        }
        if (vm.ShowProxyAuth)
        {
            if (SocksUserBox.Text.Length > 0) vm.SetProxyUsername(SocksUserBox.Text);
            if (SocksPassBox.Password.Length > 0)
            {
                vm.SetProxyPassword(SocksPassBox.Password);
                SocksPassBox.Clear();
            }
        }

        // Validate the final object (validator re-reads the credential store for refs presence).
        var validation = new ProfileValidator().Validate(candidate);
        if (!validation.IsValid)
        {
            vm.ValidationText = validation.Describe();
            vm.IsValid = false;
            MessageBox.Show(this, "The profile has validation problems:\n\n" + validation.Describe(),
                "HAZEM Tunnel", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        DialogResult = true;
        Close();
    }
}
