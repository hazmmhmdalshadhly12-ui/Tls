using System.Collections.Concurrent;
using HAZEM.Tunnel.Core.Models;

namespace HAZEM.Tunnel.Core.Statistics;

/// <summary>
/// Thread-safe byte/connection counters with 1-second throughput sampling.
/// Uses Interlocked only — no locks on the hot path; samples are produced by a timer task.
/// </summary>
public sealed class StatisticsTracker : IDisposable
{
    private long _bytesSent;
    private long _bytesReceived;
    private long _activeConnections;
    private long _totalConnections;
    private long _reconnectCount;
    private long _lastSampleSent;
    private long _lastSampleReceived;

    private DateTimeOffset? _connectedSince;
    private readonly object _stateLock = new();
    private readonly ITimer? _timer;
    private readonly ConcurrentQueue<ThroughputSample> _samples = new();
    private readonly TimeSpan _interval;
    private const int MaxSamples = 600; // 10 minutes at 1s

    public event Action<ThroughputSample>? SampleTaken;

    public StatisticsTracker(TimeSpan? samplingInterval = null, TimeProvider? timeProvider = null)
    {
        _interval = samplingInterval ?? TimeSpan.FromSeconds(1);
        if (_interval > TimeSpan.Zero)
        {
            timeProvider ??= TimeProvider.System;
            _timer = timeProvider.CreateTimer(_ => TakeSample(), null, _interval, _interval);
        }
    }

    public void AddBytesSent(long bytes) => Interlocked.Add(ref _bytesSent, bytes);
    public void AddBytesReceived(long bytes) => Interlocked.Add(ref _bytesReceived, bytes);

    public void OnConnectionOpened()
    {
        Interlocked.Increment(ref _activeConnections);
        Interlocked.Increment(ref _totalConnections);
    }

    public void OnConnectionClosed() => Interlocked.Decrement(ref _activeConnections);
    public void OnReconnect() => Interlocked.Increment(ref _reconnectCount);

    public void MarkConnected()
    {
        lock (_stateLock)
        {
            _connectedSince ??= DateTimeOffset.UtcNow;
        }
    }

    public void MarkDisconnected()
    {
        lock (_stateLock)
        {
            _connectedSince = null;
        }
    }

    public StatisticsSnapshot GetSnapshot()
    {
        lock (_stateLock)
        {
            return new StatisticsSnapshot(
                Interlocked.Read(ref _bytesSent),
                Interlocked.Read(ref _bytesReceived),
                Interlocked.Read(ref _activeConnections),
                Interlocked.Read(ref _totalConnections),
                Interlocked.Read(ref _reconnectCount),
                _connectedSince,
                DateTimeOffset.UtcNow);
        }
    }

    public IReadOnlyList<ThroughputSample> GetRecentSamples() => _samples.ToArray();

    /// <summary>Computes a throughput sample from the deltas since the last call. Thread-safe; used by the timer and tests.</summary>
    public ThroughputSample TakeSample()
    {
        var now = DateTimeOffset.UtcNow;
        var totalSent = Interlocked.Read(ref _bytesSent);
        var totalReceived = Interlocked.Read(ref _bytesReceived);
        var prevSent = Interlocked.Exchange(ref _lastSampleSent, totalSent);
        var prevReceived = Interlocked.Exchange(ref _lastSampleReceived, totalReceived);

        var sample = new ThroughputSample(now, Math.Max(0, totalSent - prevSent), Math.Max(0, totalReceived - prevReceived));
        _samples.Enqueue(sample);
        while (_samples.Count > MaxSamples) _samples.TryDequeue(out _);
        SampleTaken?.Invoke(sample);
        return sample;
    }

    public void Dispose() => _timer?.Dispose();
}
