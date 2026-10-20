using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Infrastructure;
using HAZEM.Tunnel.Infrastructure.Logging;
using HAZEM.Tunnel.App.Services;

namespace HAZEM.Tunnel.App.ViewModels;

/// <summary>Logs page: live tail from the in-memory ring, level filter, export + open folder.</summary>
public partial class LogsViewModel : ObservableObject
{
    private readonly InMemoryLogProvider _provider;
    private readonly IDialogService _dialogs;

    public LogsViewModel(InMemoryLogProvider provider, IDialogService dialogs)
    {
        _provider = provider;
        _dialogs = dialogs;
        provider.EventAdded += e =>
        {
            var app = System.Windows.Application.Current;
            if (app is null) return;
            app.Dispatcher.BeginInvoke(() =>
            {
                if (PassesFilter(e)) Events.Insert(0, e);
                while (Events.Count > 1000) Events.RemoveAt(Events.Count - 1);
            });
        };
        Reload();
    }

    public ObservableCollection<UiEvent> Events { get; } = new();

    [ObservableProperty] private bool _showInfo = true;
    [ObservableProperty] private bool _showWarn = true;
    [ObservableProperty] private bool _showError = true;
    [ObservableProperty] private string _filterText = string.Empty;

    partial void OnShowInfoChanged(bool value) => Reload();
    partial void OnShowWarnChanged(bool value) => Reload();
    partial void OnShowErrorChanged(bool value) => Reload();
    partial void OnFilterTextChanged(string value) => Reload();

    private bool PassesFilter(UiEvent e)
    {
        if (e.Level == "INFO" && !ShowInfo) return false;
        if (e.Level == "WARN" && !ShowWarn) return false;
        if ((e.Level == "ERROR" || e.Level == "FATAL") && !ShowError) return false;
        if (FilterText.Length > 0 && !e.Message.Contains(FilterText, StringComparison.OrdinalIgnoreCase)) return false;
        return true;
    }

    [RelayCommand]
    private void Reload()
    {
        Events.Clear();
        foreach (var e in _provider.Snapshot().Reverse())
            if (PassesFilter(e)) Events.Add(e);
    }

    [RelayCommand]
    private void Clear()
    {
        Events.Clear();
        _dialogs.Info("Logs", "Cleared the visible list. The file log on disk is untouched.");
    }

    [RelayCommand]
    private void Export()
    {
        var path = _dialogs.SaveTextFile("Export visible events", "Text log (*.txt)|*.txt", "hazem-events.txt");
        if (path is null) return;
        try
        {
            File.WriteAllLines(path, Events.Select(e => $"{e.Timestamp:yyyy-MM-dd HH:mm:ss} {e.Level} {e.Message}"));
            _dialogs.Info("Export", $"Exported {Events.Count} events to {path}");
        }
        catch (Exception ex)
        {
            _dialogs.Error("Export failed", ex.Message);
        }
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = AppPaths.Logs,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            _dialogs.Error("HAZEM Tunnel", "Could not open the folder: " + ex.Message);
        }
    }
}
