namespace HAZEM.Tunnel.Core.Models;

/// <summary>Instantaneous counters snapshot (thread-safe read).</summary>
public sealed record StatisticsSnapshot(
    long TotalBytesSent,
    long TotalBytesReceived,
    long ActiveConnections,
    long TotalConnections,
    long ReconnectCount,
    DateTimeOffset? ConnectedSince,
    DateTimeOffset UtcTime);

/// <summary>One throughput sample (bytes/sec measured over the sampling window).</summary>
public sealed record ThroughputSample(DateTimeOffset Timestamp, double SendBytesPerSecond, double ReceiveBytesPerSecond);
