using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace HAZEM.Tunnel.Networking.Transports;

/// <summary>
/// A minimal, explicit HTTP CONNECT bridge over an existing TLS stream.
/// Used by TlsSshTransport: SSH.NET talks ProxyTypes.Http to this local bridge,
/// and the bridge relays <c>CONNECT {ssh-host}:{ssh-port} HTTP/1.1</c> inside the TLS channel.
/// Layering stays visible: TCP → TLS(SNI) → HTTP-CONNECT → SSH.
/// The bridge MUST stay alive as long as SSH traffic flows (ForwardedPortLocal re-uses it for every channel).
/// </summary>
public sealed class LocalTlsBridge : IAsyncDisposable
{
    private readonly Socket _listener;
    private readonly Stream _tlsStream; // owned by the transport that created this bridge
    private readonly string _connectTargetHost;
    private readonly int _connectTargetPort;
    private readonly string _httpHost;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _pipes = new();
    private readonly object _pipeLock = new();
    private Task? _acceptLoop;

    public int BoundPort { get; }

    public LocalTlsBridge(Stream tlsStream, string connectTargetHost, int connectTargetPort, string httpHost)
    {
        _tlsStream = tlsStream;
        _connectTargetHost = connectTargetHost;
        _connectTargetPort = connectTargetPort;
        _httpHost = httpHost;
        _listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        _listener.Listen(8);
        BoundPort = ((IPEndPoint)_listener.LocalEndPoint!).Port;
    }

    public void Start()
    {
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener.AcceptAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            lock (_pipeLock) _pipes.Add(Task.Run(() => ServeAsync(client, _cts.Token)));
        }
    }

    private async Task ServeAsync(Socket client, CancellationToken ct)
    {
        try
        {
            client.NoDelay = true;
            await using var clientStream = new NetworkStream(client, ownsSocket: true);

            // 1. Read SSH.NET's CONNECT request (we ignore its target; we dial the configured one).
            _ = await ReadUntilAsync(clientStream, "\r\n\r\n", 8192, ct).ConfigureAwait(false);

            var sb = new StringBuilder();
            sb.Append("CONNECT ").Append(_connectTargetHost).Append(':').Append(_connectTargetPort).Append(" HTTP/1.1\r\n");
            sb.Append("Host: ").Append(_httpHost).Append("\r\n");
            sb.Append("Proxy-Connection: keep-alive\r\n\r\n");
            var bytes = Encoding.ASCII.GetBytes(sb.ToString());
            await _tlsStream.WriteAsync(bytes.AsMemory(0, bytes.Length), ct).ConfigureAwait(false);
            await _tlsStream.FlushAsync(ct).ConfigureAwait(false);

            // 2. Relay the TLS-side response verbatim; only 200 continues into relay mode.
            var response = await ReadUntilAsync(_tlsStream, "\r\n\r\n", 8192, ct).ConfigureAwait(false);
            var respBytes = Encoding.ASCII.GetBytes(response);
            await clientStream.WriteAsync(respBytes.AsMemory(0, respBytes.Length), ct).ConfigureAwait(false);
            await clientStream.FlushAsync(ct).ConfigureAwait(false);
            if (!response.Contains(" 200", StringComparison.Ordinal)) return;

            // 3. Blind bidirectional relay until either side closes.
            using var relayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var t1 = PumpAsync(_tlsStream, clientStream, relayCts.Token);
            var t2 = PumpAsync(clientStream, _tlsStream, relayCts.Token);
            await Task.WhenAny(t1, t2).ConfigureAwait(false);
            relayCts.Cancel();
        }
        catch
        {
            // Bridge pipe failed quietly; the engine restarts the whole tunnel on failure.
        }
    }

    private static async Task PumpAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buffer = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await from.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
                if (n <= 0) break;
                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch { /* normal during teardown */ }
        finally
        {
            try { to.Close(); } catch { }
        }
    }

    private static async Task<string> ReadUntilAsync(Stream stream, string terminator, int maxBytes, CancellationToken ct)
    {
        var sb = new StringBuilder();
        var buffer = new byte[1024];
        while (!sb.ToString().EndsWith(terminator, StringComparison.Ordinal))
        {
            var n = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), ct).ConfigureAwait(false);
            if (n <= 0) break;
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            if (sb.Length > maxBytes) throw new IOException("HTTP header too large in TLS bridge.");
        }
        return sb.ToString();
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        try { _listener.Dispose(); } catch { }
        Task[] pending;
        lock (_pipeLock) pending = _pipes.ToArray();
        foreach (var p in pending)
        {
            try { await p.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); } catch { }
        }
        _cts.Dispose();
    }
}
