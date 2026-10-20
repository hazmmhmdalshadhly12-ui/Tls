using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.App.Services;

namespace HAZEM.Tunnel.App.ViewModels;

/// <summary>Settings page: log retention, health interval, security switch (persisted immediately).</summary>
public partial class SettingsViewModel : ObservableObject
{
    private readonly IAppSettingsStore _store;
    private readonly IDialogService _dialogs;

    public SettingsViewModel(IAppSettingsStore store, IDialogService dialogs)
    {
        _store = store;
        _dialogs = dialogs;
        var s = store.Load();
        _logRetentionDays = s.LogRetentionDays;
        _logMaxSizeMb = (int)(s.LogMaxFileSizeBytes / (1024 * 1024));
        _healthIntervalSeconds = s.HealthCheckIntervalSeconds;
        _redactSecrets = s.RedactSecretsInLogs;
    }

    [ObservableProperty] private int _logRetentionDays;
    [ObservableProperty] private int _logMaxSizeMb;
    [ObservableProperty] private int _healthIntervalSeconds;
    [ObservableProperty] private bool _redactSecrets;

    [RelayCommand]
    private void Save()
    {
        if (LogRetentionDays is < 1 or > 365)
        {
            _dialogs.Error("Settings", "Log retention must be between 1 and 365 days.");
            return;
        }
        if (LogMaxSizeMb is < 1 or > 500)
        {
            _dialogs.Error("Settings", "Log max file size must be between 1 and 500 MB.");
            return;
        }
        if (HealthIntervalSeconds is < 10 or > 3600)
        {
            _dialogs.Error("Settings", "Health check interval must be between 10 and 3600 seconds.");
            return;
        }
        _store.Save(new AppSettings
        {
            LogRetentionDays = LogRetentionDays,
            LogMaxFileSizeBytes = LogMaxSizeMb * 1024L * 1024L,
            HealthCheckIntervalSeconds = HealthIntervalSeconds,
            RedactSecretsInLogs = RedactSecrets
        });
        _dialogs.Info("Settings", "Settings saved. Log settings apply on next launch.");
    }
}

/// <summary>Tunnel Settings page: explains what the tunnel does/does not route — honest scope.</summary>
public partial class TunnelSettingsViewModel : ObservableObject
{
    public string ProxyAddressExample { get; } = "127.0.0.1:1080";

    [RelayCommand]
    private void CopyProxyAddress()
    {
        try { System.Windows.Clipboard.SetText(ProxyAddressExample); } catch { /* clipboard can be locked */ }
    }
}
