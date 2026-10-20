using System.Windows;

namespace HAZEM.Tunnel.App.Views;

public partial class RenameDialog : Window
{
    public RenameDialog(string current)
    {
        InitializeComponent();
        NameBox.Text = current;
        NameBox.SelectAll();
        NameBox.Focus();
    }

    public string Value => NameBox.Text.Trim();

    private void OnOk(object sender, RoutedEventArgs e)
    {
        if (Value.Length == 0) return;
        DialogResult = true;
        Close();
    }
}
