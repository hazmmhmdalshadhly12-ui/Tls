using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace HAZEM.Tunnel.App.Services;

/// <summary>Thin abstraction over file/message dialogs (unit-testable seam).</summary>
public interface IDialogService
{
    bool Confirm(string title, string message);
    void Info(string title, string message);
    void Error(string title, string message);
    string? SaveTextFile(string title, string filter, string defaultName);
    string? OpenTextFile(string title, string filter);
}

public sealed class DialogService : IDialogService
{
    public bool Confirm(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    public void Info(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);

    public void Error(string title, string message) =>
        MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);

    public string? SaveTextFile(string title, string filter, string defaultName)
    {
        var dlg = new SaveFileDialog { Title = title, Filter = filter, FileName = defaultName };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }

    public string? OpenTextFile(string title, string filter)
    {
        var dlg = new OpenFileDialog { Title = title, Filter = filter };
        return dlg.ShowDialog() == true ? dlg.FileName : null;
    }
}
