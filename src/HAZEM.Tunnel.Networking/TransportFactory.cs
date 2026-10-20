using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Networking.Ssh;
using HAZEM.Tunnel.Networking.Tls;
using HAZEM.Tunnel.Networking.Transports;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Networking;

/// <summary>Creates the right transport for a profile, wiring explicit dependencies.</summary>
public sealed class TransportFactory
{
    private readonly TlsConnector _tlsConnector;
    private readonly SshConnector _sshConnector;
    private readonly SshHostKeyVerifier _hostKeyVerifier;
    private readonly ILoggerFactory _loggerFactory;

    /// <summary>Certificate info of the most recent TLS handshake (for the UI cert summary).</summary>
    public CertificateInfo? LastCertificate { get; private set; }

    public TransportFactory(TlsConnector tlsConnector, SshConnector sshConnector,
        SshHostKeyVerifier hostKeyVerifier, ILoggerFactory loggerFactory)
    {
        _tlsConnector = tlsConnector;
        _sshConnector = sshConnector;
        _hostKeyVerifier = hostKeyVerifier;
        _loggerFactory = loggerFactory;
    }

    /// <summary>
    /// Builds a live transport. TlsSsh composes TLS (SNI + validation) then SSH through a bridge;
    /// Ssh dials SSH directly; WebSocket opens its own session; TlsDirect is diagnostics-only.
    /// </summary>
    public async Task<ITransport> CreateAsync(TunnelProfile profile, CancellationToken ct)
    {
        switch (profile.TransportKind)
        {
            case TransportKind.TlsDirect:
            {
                var (stream, info) = await _tlsConnector.ConnectAsync(profile.Tls, ct).ConfigureAwait(false);
                LastCertificate = info;
                return new TlsDirectTransport(stream, _loggerFactory.CreateLogger<TlsDirectTransport>());
            }
            case TransportKind.Ssh:
            {
                var (client, _, _) = await _sshConnector.ConnectAsync(profile.Ssh, ct).ConfigureAwait(false);
                return new SshTransport(client, profile.Ssh, _loggerFactory.CreateLogger<SshTransport>());
            }
            case TransportKind.TlsSsh:
            {
                var (tlsStream, cert) = await _tlsConnector.ConnectAsync(profile.Tls, ct).ConfigureAwait(false);
                LastCertificate = cert;
                try
                {
                    return new TlsSshTransport(tlsStream, cert, profile.Ssh, _sshConnector, _hostKeyVerifier,
                        _loggerFactory.CreateLogger<TlsSshTransport>());
                }
                catch
                {
                    await tlsStream.DisposeAsync().ConfigureAwait(false);
                    throw;
                }
            }
            case TransportKind.WebSocket:
                return new WebSocketTransport(profile.WebSocket, _loggerFactory.CreateLogger<WebSocketTransport>());
            default:
                throw new TunnelException(TunnelErrorCategory.Configuration,
                    $"Unsupported transport kind {profile.TransportKind}.");
        }
    }
}
