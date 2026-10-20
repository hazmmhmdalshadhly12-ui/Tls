using System.Buffers;
using System.Linq;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Statistics;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Networking.Proxy;

/// <summary>
/// Local SOCKS5 proxy server (RFC 1928 + RFC 1929 user/pass auth).
/// - Binds to the profile's BindAddress (validation forbids non-loopback without AllowLan).
/// - Each client connection is handed to <paramref name="pipeHandler"/> which returns the
///   upstream Stream for the requested destination (the tunnel). The proxy itself never dials the internet.
/// - Bounded concurrency, idle watchdog, clean shutdown. Real byte counting via the relay.
/// </summary>
public sealed class Socks5ProxyServer : IAsyncDisposable
{
    private readonly LocalProxyOptions _options;
    private readonly Func<string, int, CancellationToken, Task<Stream>> _pipeHandler;
    private readonly Func<string, string, bool>? _credentialChecker;
    private readonly StatisticsTracker? _stats;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _slots;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _clients = new();
    private readonly object _clientsLock = new();
    private Socket? _listener;
    private Task? _acceptLoop;

    public Socks5ProxyServer(LocalProxyOptions options,
        Func<string, int, CancellationToken, Task<Stream>> pipeHandler,
        Func<string, string, bool>? credentialChecker,
        StatisticsTracker? stats,
        ILogger logger)
    {
        _options = options;
        _pipeHandler = pipeHandler;
        _credentialChecker = credentialChecker;
        _stats = stats;
        _logger = logger;
        _slots = new SemaphoreSlim(Math.Max(1, options.MaxClients), Math.Max(1, options.MaxClients));
    }

    public IPEndPoint? EndPoint { get; private set; }

    public void Start()
    {
        if (_listener is not null) throw new InvalidOperationException("Proxy already started.");
        if (!IPAddress.TryParse(_options.BindAddress, out var bind))
            throw new TunnelException(TunnelErrorCategory.Configuration, $"Invalid bind address '{_options.BindAddress}'.");

        _listener = new Socket(bind.AddressFamily, SocketType.Stream, ProtocolType.Tcp);
        _listener.Bind(new IPEndPoint(bind, _options.Port));
        _listener.Listen(Math.Max(8, _options.MaxClients));
        EndPoint = (IPEndPoint)_listener.LocalEndPoint!;
        _logger.LogInformation("SOCKS5 proxy listening on {Ep} (maxClients={Max}, auth={Auth})",
            EndPoint, _options.MaxClients, _options.AuthMode);
        _acceptLoop = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            Socket client;
            try
            {
                client = await _listener!.AcceptAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (SocketException) { break; }

            if (!await _slots.WaitAsync(0, _cts.Token).ConfigureAwait(false))
            {
                // At capacity: send server failure per RFC and close.
                try
                {
                    client.NoDelay = true;
                    var reply = new byte[] { 0x05, 0x01, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
                    await client.SendAsync(reply, SocketFlags.None).ConfigureAwait(false);
                    client.Dispose();
                }
                catch { client.Dispose(); }
                continue;
            }

            _ = Task.Run(async () =>
            {
                try { await ServeClientAsync(client, _cts.Token).ConfigureAwait(false); }
                catch { /* per-client failures are logged inside ServeClientAsync */ }
                finally
                {
                    _slots.Release();
                    lock (_clientsLock) { /* housekeeping */ }
                }
            });
        }
    }

    private async Task ServeClientAsync(Socket client, CancellationToken serverCt)
    {
        long bytesIn = 0, bytesOut = 0;
        client.NoDelay = true;
        try
        {
            await using var stream = new NetworkStream(client, ownsSocket: true);
            using var idleCts = CancellationTokenSource.CreateLinkedTokenSource(serverCt);
            idleCts.CancelAfter(_options.IdleTimeout);

            // --- RFC 1928 method negotiation ---
            var head = await ReadExactlyAsync(stream, 2, idleCts.Token).ConfigureAwait(false);
            if (head[0] != 0x05)
            {
                _logger.LogWarning("SOCKS5: non-SOCKS5 client dropped (first byte 0x{X2})", head[0]);
                return;
            }
            var nMethods = head[1];
            var methods = await ReadExactlyAsync(stream, nMethods, idleCts.Token).ConfigureAwait(false);

            byte chosen = 0xFF;
            if (methods.Contains((byte)0x00) && _options.AuthMode == ProxyAuthMode.None) chosen = 0x00;
            else if (methods.Contains((byte)0x02) && _options.AuthMode == ProxyAuthMode.UsernamePassword) chosen = 0x02;

            if (chosen == 0xFF)
            {
                await stream.WriteAsync(new byte[] { 0x05, 0xFF }, idleCts.Token).ConfigureAwait(false);
                _logger.LogWarning("SOCKS5: no acceptable auth method offered by client.");
                return;
            }
            await stream.WriteAsync(new byte[] { 0x05, chosen }, idleCts.Token).ConfigureAwait(false);

            // --- RFC 1929 user/pass ---
            if (chosen == 0x02)
            {
                var vlen = await ReadExactlyAsync(stream, 2, idleCts.Token).ConfigureAwait(false);
                var user = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, vlen[1], idleCts.Token).ConfigureAwait(false));
                var plen = await ReadExactlyAsync(stream, 1, idleCts.Token).ConfigureAwait(false);
                var pass = Encoding.UTF8.GetString(await ReadExactlyAsync(stream, plen[0], idleCts.Token).ConfigureAwait(false));

                var ok = _credentialChecker?.Invoke(user, pass) ?? false;
                await stream.WriteAsync(new byte[] { 0x01, (byte)(ok ? 0 : 1) }, idleCts.Token).ConfigureAwait(false);
                if (!ok)
                {
                    _logger.LogWarning("SOCKS5: authentication failed for user '{User}'.", user);
                    return;
                }
            }

            // --- RFC 1928 request ---
            var req = await ReadExactlyAsync(stream, 4, idleCts.Token).ConfigureAwait(false);
            if (req[0] != 0x05) return;
            var cmd = req[1];
            if (cmd != 0x01) // only CONNECT is supported (no BIND/UDP ASSOCIATE)
            {
                await WriteReplyAsync(stream, 0x07, idleCts.Token).ConfigureAwait(false);
                _logger.LogWarning("SOCKS5: unsupported command 0x{Cmd:X2}.", cmd);
                return;
            }

            string destHost;
            var atyp = req[3];
            switch (atyp)
            {
                case 0x01:
                    var v4 = await ReadExactlyAsync(stream, 4, idleCts.Token).ConfigureAwait(false);
                    destHost = new IPAddress(v4).ToString();
                    break;
                case 0x03:
                    var len = await ReadExactlyAsync(stream, 1, idleCts.Token).ConfigureAwait(false);
                    var name = await ReadExactlyAsync(stream, len[0], idleCts.Token).ConfigureAwait(false);
                    destHost = Encoding.ASCII.GetString(name);
                    break;
                case 0x04:
                    var v6 = await ReadExactlyAsync(stream, 16, idleCts.Token).ConfigureAwait(false);
                    destHost = new IPAddress(v6).ToString();
                    break;
                default:
                    await WriteReplyAsync(stream, 0x08, idleCts.Token).ConfigureAwait(false);
                    return;
            }
            var portBytes = await ReadExactlyAsync(stream, 2, idleCts.Token).ConfigureAwait(false);
            var destPort = (portBytes[0] << 8) | portBytes[1];

            // Hand the pipe to the tunnel: the handler returns the upstream stream for destHost:destPort.
            Stream upstream;
            try
            {
                upstream = await _pipeHandler(destHost, destPort, idleCts.Token).ConfigureAwait(false);
            }
            catch (TunnelException te)
            {
                _logger.LogWarning("SOCKS5 CONNECT to {Host}:{Port} failed via tunnel: {Category} {Message}",
                    destHost, destPort, te.Category, te.Message);
                var code = te.Category switch
                {
                    TunnelErrorCategory.NetworkUnreachable or TunnelErrorCategory.Dns => (byte)0x04, // host unreachable
                    TunnelErrorCategory.ConnectionRefused => (byte)0x05,                             // refused
                    TunnelErrorCategory.Timeout => (byte)0x06,                                       // TTL expired
                    _ => (byte)0x01                                                                  // general failure
                };
                await WriteReplyAsync(stream, code, idleCts.Token).ConfigureAwait(false);
                return;
            }
            catch (Exception ex)
            {
                _logger.LogWarning("SOCKS5 CONNECT to {Host}:{Port} failed: {Message}", destHost, destPort, ex.Message);
                await WriteReplyAsync(stream, 0x01, idleCts.Token).ConfigureAwait(false);
                return;
            }

            // Success reply (BND.ADDR all-zero is acceptable in practice).
            await WriteReplyAsync(stream, 0x00, idleCts.Token).ConfigureAwait(false);
            _logger.LogInformation("SOCKS5 CONNECT {Host}:{Port} established.", destHost, destPort);
            _stats?.OnConnectionOpened();

            try
            {
                // Real, continuous byte counting — upstream-bound bytes count as SENT as they leave the machine.
                await Relay.RelayAsync(stream, upstream,
                    b => { bytesOut += b; _stats?.AddBytesSent(b); },
                    b => { bytesIn += b; _stats?.AddBytesReceived(b); },
                    idleCts.Token).ConfigureAwait(false);
            }
            finally
            {
                _stats?.OnConnectionClosed();
            }
            _logger.LogDebug("SOCKS5 pipe {Host}:{Port} closed: {Sent} bytes out, {Recv} bytes in.",
                destHost, destPort, Interlocked.Read(ref bytesOut), Interlocked.Read(ref bytesIn));
        }
        catch (OperationCanceledException) when (serverCt.IsCancellationRequested)
        {
            // server shutting down
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("SOCKS5 client idle timeout or cancellation.");
        }
        catch (Exception ex)
        {
            _logger.LogDebug("SOCKS5 client ended: {Message}", ex.Message);
        }
    }

    private static async Task WriteReplyAsync(NetworkStream s, byte code, CancellationToken ct)
    {
        // VER=5 REP=code RSV=0 ATYP=IPv4 BND.ADDR=0 BND.PORT=0
        var reply = new byte[] { 0x05, code, 0x00, 0x01, 0, 0, 0, 0, 0, 0 };
        await s.WriteAsync(reply.AsMemory(0, reply.Length), ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    private static async Task<byte[]> ReadExactlyAsync(NetworkStream s, int count, CancellationToken ct)
    {
        if (count == 0) return Array.Empty<byte>();
        var buffer = ArrayPool<byte>.Shared.Rent(count);
        try
        {
            var read = 0;
            while (read < count)
            {
                var n = await s.ReadAsync(buffer.AsMemory(read, count - read), ct).ConfigureAwait(false);
                if (n <= 0) throw new EndOfStreamException("SOCKS5 client disconnected mid-message.");
                read += n;
            }
            var result = new byte[count];
            Buffer.BlockCopy(buffer, 0, result, 0, count);
            return result;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_listener is not null)
        {
            try { _listener.Dispose(); } catch { }
            _listener = null;
        }
        if (_acceptLoop is not null)
        {
            try { await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
        Task[] snapshot;
        lock (_clientsLock) { snapshot = Array.Empty<Task>(); }
        foreach (var t in snapshot)
        {
            try { await t.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); } catch { }
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _cts.Dispose();
        _slots.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}

/// <summary>Relay helper kept separate so tests can drive raw streams with live counters.</summary>
public static class Relay
{
    /// <summary>Relays both directions; calls the counters with the REAL number of bytes moved per write.</summary>
    public static async Task RelayAsync(Stream clientSide, Stream upstream,
        Action<long>? clientToUpstream, Action<long>? upstreamToClient, CancellationToken ct)
    {
        var t1 = PumpAsync(clientSide, upstream, clientToUpstream, ct);
        var t2 = PumpAsync(upstream, clientSide, upstreamToClient, ct);
        await Task.WhenAny(t1, t2).ConfigureAwait(false);
        try { await Task.WhenAll(t1, t2).WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch (TimeoutException) { }
    }

    private static async Task PumpAsync(Stream from, Stream to, Action<long>? count, CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(16384);
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await from.ReadAsync(buffer.AsMemory(0, 16384), ct).ConfigureAwait(false);
                if (n <= 0) break;
                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
                count?.Invoke(n);
            }
        }
        catch { /* peer closed / cancel */ }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            try { to.Close(); } catch { }
        }
    }
}
