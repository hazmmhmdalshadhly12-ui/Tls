using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Statistics;

namespace HAZEM.Tunnel.Core.Interfaces;
/// <summary>
/// A byte-carrying tunnel transport. Implementations return a live Stream whose traffic
/// is relayed through the remote side. Streams are single-use per pipe.
/// </summary>
public interface ITransport : IAsyncDisposable
{
    TransportKind Kind { get; }
    /// <summary>Opens the transport end-to-end (per its kind) and returns the relay stream.</summary>
    Task<Stream> OpenAsync(CancellationToken ct);
}

/// <summary>Connects a TCP+TLS session with explicit SNI and certificate validation.</summary>
public interface ITlsConnector
{
    Task<(Stream Stream, CertificateInfo Info)> ConnectAsync(TlsOptions options, CancellationToken ct);
}

/// <summary>Tests an SSH server: reaches it, authenticates, reports host key fingerprint.</summary>
public interface ISshConnector
{
    Task<SshTestResult> TestAsync(SshOptions options, CancellationToken ct);
}

/// <summary>Runs a health check for the currently active profile (through the tunnel first).</summary>
public interface IHealthChecker
{
    Task<HealthCheckResult> CheckAsync(TunnelProfile profile, CancellationToken ct);
}

/// <summary>The staged tunnel engine owned by the UI.</summary>
public interface ITunnelEngine : IAsyncDisposable
{
    ConnectionState State { get; }
    ConnectionStage Stage { get; }
    CertificateInfo? LastCertificate { get; }
    event Action<ConnectionState>? StateChanged;
    event Action<ConnectionStage>? StageChanged;
    event Action<string, string>? EventLogged; // level, message
    event Action<HealthCheckResult>? HealthChecked;
    Task ConnectAsync(TunnelProfile profile, CancellationToken ct);
    Task DisconnectAsync();
    StatisticsTracker Statistics { get; }
}
