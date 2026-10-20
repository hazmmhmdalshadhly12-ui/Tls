using System.IO;
using System.Net.Http;
using System.Net.WebSockets;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Networking.Transports;

/// <summary>
/// WebSocket transport (ws:// or wss://). Each OpenAsync creates a FRESH WebSocket session;
/// wss:// gets standard TLS validation (never disabled); explicit headers are validated first.
/// </summary>
public sealed class WebSocketTransport : ITransport
{
    private readonly WebSocketOptions _options;
    private readonly ILogger<WebSocketTransport> _logger;

    public WebSocketTransport(WebSocketOptions options, ILogger<WebSocketTransport> logger)
    {
        _options = options;
        _logger = logger;
    }

    public TransportKind Kind => TransportKind.WebSocket;

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public async Task<Stream> OpenAsync(CancellationToken ct)
    {
        if (!Uri.TryCreate(_options.Url, UriKind.Absolute, out var uri))
            throw new TunnelException(TunnelErrorCategory.Configuration, $"Invalid WebSocket URL: '{_options.Url}'");
        if (uri.Scheme is not ("ws" or "wss"))
            throw new TunnelException(TunnelErrorCategory.Configuration, "WebSocket scheme must be ws:// or wss://");

        ValidateHeaders();

        var ws = new ClientWebSocket();
        try
        {
            foreach (var (name, value) in _options.Headers)
            {
                if (name.Equals("Host", StringComparison.OrdinalIgnoreCase))
                    continue; // derived from URI by .NET
                ws.Options.SetRequestHeader(name, value);
            }
            if (!string.IsNullOrWhiteSpace(_options.Subprotocol))
                ws.Options.AddSubProtocol(_options.Subprotocol);
            ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(_options.HandshakeTimeout);
            await ws.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            ws.Dispose();
            throw new TunnelException(TunnelErrorCategory.Timeout,
                $"WebSocket handshake with {uri.Host} timed out after {_options.HandshakeTimeout.TotalSeconds:0}s.");
        }
        catch (WebSocketException ex)
        {
            ws.Dispose();
            var category = uri.Scheme == "wss" && ex.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase)
                ? TunnelErrorCategory.Certificate
                : TunnelErrorCategory.WebSocketHandshake;
            throw new TunnelException(category, $"WebSocket handshake with {uri.Host}:{uri.Port} failed: {ex.Message}", ex);
        }
        catch (Exception)
        {
            ws.Dispose();
            throw;
        }

        _logger.LogInformation("WebSocket session open: {Url} (state={State})", _options.Url, ws.State);
        return new WebSocketStream(ws);
    }

    private void ValidateHeaders()
    {
        foreach (var name in _options.Headers.Keys)
        {
            if (name.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
                throw new TunnelException(TunnelErrorCategory.Configuration,
                    $"Header '{name}' is reserved by the WebSocket handshake and cannot be set.");
        }
    }
}
