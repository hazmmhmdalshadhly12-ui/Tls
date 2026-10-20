using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HAZEM.Tunnel.App.Services;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;

namespace HAZEM.Tunnel.App.ViewModels;

/// <summary>
/// Dashboard state machine: mirrors TunnelEngine state/stage, shows REAL counters and
/// honest health check results. "Connected" appears only when the engine says so.
/// </summary>
public partial class DashboardViewModel : ObservableObject
{
    private readonly ITunnelEngine _engine;
    private readonly IProfileStore _profiles;
    private readonly IAppSettingsStore _settings;
    private readonly IDialogService _dialogs;
    private readonly System.Timers.Timer _uiTimer;

    public DashboardViewModel(ITunnelEngine engine, IProfileStore profiles, IAppSettingsStore settings,
        IDialogService dialogs)
    {
        _engine = engine;
        _profiles = profiles;
        _settings = settings;
        _dialogs = dialogs;

        _engine.StateChanged += OnEngineStateChanged;
        _engine.StageChanged += s => { _stage = s; RaiseState(); };
        _engine.EventLogged += (lvl, msg) => AddEvent(lvl, msg);
        _engine.HealthChecked += r => System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            LastHealth = $"{(r.Success ? "✔" : "✘")} {(r.HttpStatusCode is { } c ? $"HTTP {c}, " : "")}{r.Latency.TotalMilliseconds:0} ms — {r.Detail}");

        _uiTimer = new System.Timers.Timer(1000);
        _uiTimer.Elapsed += (_, _) => RefreshCounters();
        _uiTimer.Start();

        LoadProfileNames();
        AddEvent("INFO", "Dashboard ready.");
    }

    // ---- profile selection ----
    public ObservableCollection<string> ProfileNames { get; } = new();

    [ObservableProperty]
    private string _selectedProfile = string.Empty;

    private void LoadProfileNames()
    {
        ProfileNames.Clear();
        foreach (var p in _profiles.LoadAll().OrderBy(p => p.Name))
            ProfileNames.Add(p.Name);
        if (SelectedProfile.Length == 0 && ProfileNames.Count > 0)
            SelectedProfile = ProfileNames[0];
    }

    [RelayCommand]
    private void RefreshProfiles() => LoadProfileNames();

    // ---- state surface ----
    private ConnectionState _state = ConnectionState.Disconnected;
    private ConnectionStage _stage = ConnectionStage.Idle;

    private void OnEngineStateChanged(ConnectionState s)
    {
        _state = s;
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RaiseState();
            if (s == ConnectionState.Connected) LoadProfileNames();
        });
    }

    private void RaiseState()
    {
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(StateColorKey));
        OnPropertyChanged(nameof(StageText));
        OnPropertyChanged(nameof(IsBusy));
        OnPropertyChanged(nameof(CanConnect));
        OnPropertyChanged(nameof(CanDisconnect));
    }

    public string StateText => _state switch
    {
        ConnectionState.Disconnected => "Disconnected",
        ConnectionState.Connecting => "Connecting…",
        ConnectionState.Connected => "Connected",
        ConnectionState.Reconnecting => "Reconnecting…",
        ConnectionState.Failed => "Failed",
        _ => _state.ToString()
    };

    public string StateColorKey => _state switch
    {
        ConnectionState.Connected => "Good",
        ConnectionState.Failed => "Bad",
        ConnectionState.Connecting or ConnectionState.Reconnecting => "Warn",
        _ => "Dim"
    };

    public string StageText => _state == ConnectionState.Connected
        ? $"Stage: {_engine.Stage}"
        : $"Stage: {_stage}";

    public bool IsBusy => _state is ConnectionState.Connecting or ConnectionState.Reconnecting;
    public bool CanConnect => _state is ConnectionState.Disconnected or ConnectionState.Failed && SelectedProfile.Length > 0;
    public bool CanDisconnect => _state is not ConnectionState.Disconnected;

    // ---- live counters (real values only) ----
    public string BytesSent => FormatBytes(Snapshot().TotalBytesSent);
    public string BytesRecv => FormatBytes(Snapshot().TotalBytesReceived);
    public string ActiveConns => Snapshot().ActiveConnections.ToString();
    public string TotalConns => Snapshot().TotalConnections.ToString();
    public string Reconnects => Snapshot().ReconnectCount.ToString();
    public string Uptime => Snapshot().ConnectedSince is { } since
        ? (DateTimeOffset.UtcNow - since).ToString(@"hh\:mm\:ss")
        : "—";

    [ObservableProperty]
    private string _lastHealth = "not run yet";

    private StatisticsSnapshot Snapshot() => _engine.Statistics.GetSnapshot();

    private void RefreshCounters()
    {
        OnPropertyChanged(nameof(BytesSent));
        OnPropertyChanged(nameof(BytesRecv));
        OnPropertyChanged(nameof(ActiveConns));
        OnPropertyChanged(nameof(TotalConns));
        OnPropertyChanged(nameof(Reconnects));
        OnPropertyChanged(nameof(Uptime));
    }

    private static string FormatBytes(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):F1} KB",
        _ => $"{b} B"
    };

    // ---- commands ----
    [RelayCommand]
    private async Task ConnectAsync()
    {
        var profile = _profiles.LoadAll().FirstOrDefault(p => p.Name == SelectedProfile);
        if (profile is null)
        {
            _dialogs.Error("HAZEM Tunnel", $"Profile '{SelectedProfile}' no longer exists. Refresh the list.");
            return;
        }
        try
        {
            AddEvent("INFO", $"Connecting '{profile.Name}' ({profile.TransportKind})…");
            await _engine.ConnectAsync(profile, CancellationToken.None);
        }
        catch (Exception ex)
        {
            _dialogs.Error("HAZEM Tunnel", "Connect failed: " + ex.Message);
        }
    }

    [RelayCommand]
    private async Task DisconnectAsync()
    {
        try { await _engine.DisconnectAsync(); }
        catch (Exception ex)
        {
            _dialogs.Error("HAZEM Tunnel", "Disconnect error: " + ex.Message);
        }
    }

    // ---- event log tail ----
    public ObservableCollection<UiEvent> Events { get; } = new();

    private void AddEvent(string level, string message)
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            Events.Insert(0, new UiEvent(DateTimeOffset.Now, level, message));
            while (Events.Count > 300) Events.RemoveAt(Events.Count - 1);
        });
    }
}
