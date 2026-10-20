using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using System.Net;
using System.Net.Sockets;
using HAZEM.Tunnel.Networking.Ssh;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace HAZEM.Tunnel.Networking.Transports;

/// <summary>
/// TLS(SNI) → SSH layered transport. An HTTP CONNECT bridge runs INSIDE the TLS stream;
/// SSH.NET connects through that bridge (ProxyTypes.Http) with its own host key check and auth.
/// The bridge, SSH client and TLS stream share one lifetime: DisposeAsync tears them all down.
/// </summary>
public sealed class TlsSshTransport : ITransport
{
    private readonly Stream _tlsStream;
    private readonly CertificateInfo _certInfo;
    private readonly SshOptions _options;
    private readonly SshConnector _sshConnector;
    private readonly SshHostKeyVerifier _verifier;
    private readonly ILogger<TlsSshTransport> _logger;

    private LocalTlsBridge? _bridge;
    private SshClient? _client;
    private ForwardedPortLocal? _port;

    public TlsSshTransport(Stream tlsStream, CertificateInfo certInfo, SshOptions options,
        SshConnector sshConnector, SshHostKeyVerifier verifier, ILogger<TlsSshTransport> logger)
    {
        _tlsStream = tlsStream;
        _certInfo = certInfo;
        _options = options;
        _sshConnector = sshConnector;
        _verifier = verifier;
        _logger = logger;
    }

    public TransportKind Kind => TransportKind.TlsSsh;

    public async Task<Stream> OpenAsync(CancellationToken ct)
    {
        // 1. Local HTTP-CONNECT bridge inside the TLS channel (kept alive with the transport).
        var httpHost = _options.Host;
        var bridge = new LocalTlsBridge(_tlsStream, _options.Host, _options.Port, httpHost);
        _bridge = bridge;
        bridge.Start();

        // 2. SSH through the bridge (SSH.NET dials the local bridge as an HTTP proxy).
        var auth = _sshConnector.BuildAuthentication(_options);
        var info = new ConnectionInfo(_options.Host, _options.Port, _options.Username,
            ProxyTypes.Http, "127.0.0.1", bridge.BoundPort, null, null, auth)
        {
            Timeout = _options.ConnectTimeout,
            RetryAttempts = 1
        };
        var client = new SshClient(info);
        _client = client;
        client.HostKeyReceived += (_, e) =>
        {
            var decision = _verifier.Verify(_options.Host, _options.Port, e.HostKey, _options.HostKeyPolicy,
                _options.PinnedHostKeyFingerprint);
            e.CanTrust = decision is HostKeyDecision.Accepted or HostKeyDecision.FirstUseAccepted;
        };

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.ConnectTimeout);
            await client.ConnectAsync(timeoutCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await CleanupAsync().ConfigureAwait(false);
            throw new TunnelException(TunnelErrorCategory.SshProtocol,
                $"SSH-over-TLS failed for {_options.Host}:{_options.Port}: {ex.Message}", ex);
        }

        if (!client.IsConnected)
        {
            await CleanupAsync().ConfigureAwait(false);
            throw new TunnelException(TunnelErrorCategory.SshProtocol, "SSH-over-TLS session did not connect.");
        }
        _logger.LogInformation("SSH-over-TLS established (TLS subject {Subject})", _certInfo.Subject);

        // 3. Forwarded port per pipe through the same session.
        var port = new ForwardedPortLocal("127.0.0.1", 0u, _options.RemoteForwardHost, (uint)_options.RemoteForwardPort);
        try
        {
            client.AddForwardedPort(port);
            port.Start();
        }
        catch (Exception ex)
        {
            await CleanupAsync().ConfigureAwait(false);
            throw new TunnelException(TunnelErrorCategory.SshProtocol,
                $"Could not open forwarded channel: {ex.Message}", ex);
        }
        _port = port;

        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(IPAddress.Loopback, (int)port.BoundPort, ct).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    private async Task CleanupAsync()
    {
        if (_port is not null)
        {
            try { _port.Stop(); } catch { }
            try { _client?.RemoveForwardedPort(_port); } catch { }
            _port = null;
        }
        if (_client is not null)
        {
            try { _client.Disconnect(); } catch { }
            _client.Dispose();
            _client = null;
        }
        if (_bridge is not null)
        {
            await _bridge.DisposeAsync().ConfigureAwait(false);
            _bridge = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await CleanupAsync().ConfigureAwait(false);
        try { await _tlsStream.DisposeAsync().ConfigureAwait(false); } catch { }
    }
}
