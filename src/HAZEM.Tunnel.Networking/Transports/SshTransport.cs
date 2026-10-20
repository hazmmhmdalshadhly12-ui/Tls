using System.Net;
using System.Net.Sockets;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using Microsoft.Extensions.Logging;
using Renci.SshNet;

namespace HAZEM.Tunnel.Networking.Transports;

/// <summary>
/// Plain SSH transport: one ForwardedPortLocal per pipe, bound to an ephemeral loopback port,
/// pointed at (RemoteForwardHost, RemoteForwardPort) behind the SSH server.
/// </summary>
public sealed class SshTransport : ITransport
{
    private readonly SshClient _client;
    private readonly SshOptions _options;
    private readonly ILogger<SshTransport> _logger;

    public SshTransport(SshClient client, SshOptions options, ILogger<SshTransport> logger)
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public TransportKind Kind => TransportKind.Ssh;

    public async Task<Stream> OpenAsync(CancellationToken ct)
    {
        if (!_client.IsConnected)
            throw new TunnelException(TunnelErrorCategory.SshProtocol, "SSH session is not connected.");

        var port = new ForwardedPortLocal("127.0.0.1", 0u, _options.RemoteForwardHost, (uint)_options.RemoteForwardPort);
        try
        {
            _client.AddForwardedPort(port);
            port.Start();
            ct.ThrowIfCancellationRequested();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _client.RemoveForwardedPort(port);
            throw new TunnelException(TunnelErrorCategory.SshProtocol,
                $"Could not open SSH forwarded channel to {_options.RemoteForwardHost}:{_options.RemoteForwardPort}: {ex.Message}", ex);
        }

        _logger.LogDebug("SSH forwarded port bound on {Bound}, target {Target}:{TargetPort}",
            $"127.0.0.1:{port.BoundPort}", _options.RemoteForwardHost, _options.RemoteForwardPort);

        var boundPort = (int)port.BoundPort;

        // TCP connect to the local forwarded endpoint
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        await socket.ConnectAsync(IPAddress.Loopback, boundPort, ct).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    public async ValueTask DisposeAsync()
    {
        await Task.Yield();
        try
        {
            foreach (var port in _client.ForwardedPorts.ToArray())
            {
                try { port.Stop(); } catch { /* best effort */ }
                _client.RemoveForwardedPort(port);
            }
        }
        catch { /* session may already be gone */ }
    }
}
