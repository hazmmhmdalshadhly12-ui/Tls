using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;

namespace HAZEM.Tunnel.App.ViewModels;

/// <summary>Statistics page: real counters + real throughput samples (no synthetic data).</summary>
public partial class StatisticsViewModel : ObservableObject
{
    private readonly ITunnelEngine _engine;
    private readonly System.Timers.Timer _timer;

    public StatisticsViewModel(ITunnelEngine engine)
    {
        _engine = engine;
        _timer = new System.Timers.Timer(1000);
        _timer.Elapsed += (_, _) => Refresh();
        _timer.Start();
    }

    public ObservableCollection<ThroughputSample> Samples { get; } = new();

    [ObservableProperty] private string _totalSent = "0 B";
    [ObservableProperty] private string _totalRecv = "0 B";
    [ObservableProperty] private string _sendRate = "0 B/s";
    [ObservableProperty] private string _recvRate = "0 B/s";
    [ObservableProperty] private string _activeConns = "0";
    [ObservableProperty] private string _totalConns = "0";
    [ObservableProperty] private string _reconnects = "0";
    [ObservableProperty] private string _uptime = "—";

    private void Refresh()
    {
        var app = System.Windows.Application.Current;
        if (app is null) return;
        app.Dispatcher.BeginInvoke(() =>
        {
            var snap = _engine.Statistics.GetSnapshot();
            TotalSent = Format(snap.TotalBytesSent);
            TotalRecv = Format(snap.TotalBytesReceived);
            ActiveConns = snap.ActiveConnections.ToString();
            TotalConns = snap.TotalConnections.ToString();
            Reconnects = snap.ReconnectCount.ToString();
            Uptime = snap.ConnectedSince is { } since ? (DateTimeOffset.UtcNow - since).ToString(@"hh\:mm\:ss") : "—";

            var samples = _engine.Statistics.GetRecentSamples().ToList();
            var last = samples.LastOrDefault();
            SendRate = last is null ? "0 B/s" : Format((long)last.SendBytesPerSecond) + "/s";
            RecvRate = last is null ? "0 B/s" : Format((long)last.ReceiveBytesPerSecond) + "/s";

            Samples.Clear();
            foreach (var s in samples) Samples.Add(s);
            OnPropertyChanged(nameof(SeriesSent));
            OnPropertyChanged(nameof(SeriesRecv));
        });
    }

    /// <summary>Downsampled series for the chart (peak per bucket keeps spikes visible).</summary>
    public IReadOnlyList<double> SeriesSent => BuildSeries(s => s.SendBytesPerSecond);
    public IReadOnlyList<double> SeriesRecv => BuildSeries(s => s.ReceiveBytesPerSecond);

    private IReadOnlyList<double> BuildSeries(Func<ThroughputSample, double> pick)
    {
        var data = _engine.Statistics.GetRecentSamples();
        const int buckets = 120;
        if (data.Count == 0) return Array.Empty<double>();
        var result = new double[buckets];
        var per = Math.Max(1, (int)Math.Ceiling(data.Count / (double)buckets));
        for (var i = 0; i < data.Count; i++)
        {
            var b = Math.Min(buckets - 1, i / per);
            result[b] = Math.Max(result[b], pick(data[i]));
        }
        return result;
    }

    private static string Format(long b) => b switch
    {
        >= 1L << 30 => $"{b / (double)(1L << 30):F2} GB",
        >= 1L << 20 => $"{b / (double)(1L << 20):F2} MB",
        >= 1L << 10 => $"{b / (double)(1L << 10):F1} KB",
        _ => $"{b} B"
    };
}
