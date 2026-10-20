using System.Net;
using System.Net.Sockets;
using System.Text;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Networking.Proxy;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HAZEM.Tunnel.Networking.Tests;

/// <summary>Live loopback tests: real sockets, real bytes, zero simulation.</summary>
public class Socks5ProxyServerTests
{
    private static int FreePort()
    {
        var l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)l.LocalEndPoint!).Port;
        l.Dispose();
        return port;
    }

    private static LocalProxyOptions Options(int port, ProxyAuthMode auth = ProxyAuthMode.None) => new()
    {
        BindAddress = "127.0.0.1",
        Port = port,
        IdleTimeout = TimeSpan.FromSeconds(10),
        MaxClients = 16,
        AuthMode = auth
    };

    /// <summary>Echo "server" playing the role of the tunnel destination.</summary>
    private static async Task<(Socket Listener, int Port)> StartEchoAsync()
    {
        var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        listener.Listen(16);
        var port = ((IPEndPoint)listener.LocalEndPoint!).Port;
        _ = Task.Run(async () =>
        {
            while (true)
            {
                Socket client;
                try { client = await listener.AcceptAsync(); }
                catch { break; }
                _ = Task.Run(async () =>
                {
                    var buf = new byte[4096];
                    try
                    {
                        while (true)
                        {
                            var n = await client.ReceiveAsync(buf);
                            if (n == 0) break;
                            await client.SendAsync(buf.AsMemory(0, n));
                        }
                    }
                    catch { }
                    finally { client.Dispose(); }
                });
            }
        });
        return (listener, port);
    }

    /// <summary>Handler that "tunnels" to the echo destination the way the engine would.</summary>
    private static Func<string, int, CancellationToken, Task<Stream>> DialEcho(int echoPort) =>
        async (_, _, _) =>
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            await s.ConnectAsync(IPAddress.Loopback, echoPort);
            return new NetworkStream(s, ownsSocket: true);
        };

    private static Socks5ProxyServer StartProxy(int port, LocalProxyOptions opts,
        Func<string, int, CancellationToken, Task<Stream>> handler,
        Func<string, string, bool>? creds = null)
        => new(opts, handler, creds, null, NullLogger.Instance);

    [Fact]
    public async Task NoAuth_ConnectAndEcho_Works()
    {
        var (echo, echoPort) = await StartEchoAsync();
        var proxy = StartProxy(FreePort(), Options(FreePort()), DialEcho(echoPort));
        try
        {
            proxy.Start();
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, proxy.EndPoint!.Port);
            using var s = new NetworkStream(client, ownsSocket: false);

            // greeting: ver=5, nmethods=1, method=no-auth
            await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
            var reply = new byte[2];
            Assert.Equal(2, await ReadExactly(s, reply));
            Assert.Equal(0x05, reply[0]);
            Assert.Equal(0x00, reply[1]);

            // connect to echo via IPv4
            await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, (byte)(echoPort >> 8), (byte)(echoPort & 0xFF) });
            var connReply = new byte[10];
            Assert.Equal(10, await ReadExactly(s, connReply));
            Assert.Equal(0x00, connReply[1]); // succeeded

            // data path
            var payload = "hello through tunnel"u8.ToArray();
            await s.WriteAsync(payload);
            var back = new byte[payload.Length];
            Assert.Equal(payload.Length, await ReadExactly(s, back));
            Assert.Equal(payload, back);
        }
        finally
        {
            await proxy.DisposeAsync();
            echo.Dispose();
        }
    }

    [Fact]
    public async Task ClientOffersNoAuth_ButProfileRequiresUserPass_ServerRejects()
    {
        var (echo, echoPort) = await StartEchoAsync();
        var proxy = StartProxy(FreePort(), Options(FreePort(), ProxyAuthMode.UsernamePassword),
            DialEcho(echoPort), creds: (_, _) => true);
        try
        {
            proxy.Start();
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, proxy.EndPoint!.Port);
            using var s = new NetworkStream(client, ownsSocket: false);

            await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00 }); // offers only no-auth
            var reply = new byte[2];
            Assert.Equal(2, await ReadExactly(s, reply));
            Assert.Equal(0x05, reply[0]);
            Assert.Equal(0xFF, reply[1]); // no acceptable method
        }
        finally
        {
            await proxy.DisposeAsync();
            echo.Dispose();
        }
    }

    [Fact]
    public async Task UserPassAuth_Accepted_AndWrongRejected()
    {
        var (echo, echoPort) = await StartEchoAsync();
        var proxy = StartProxy(FreePort(), Options(FreePort(), ProxyAuthMode.UsernamePassword),
            DialEcho(echoPort), creds: (u, p) => u == "ali" && p == "secret123");
        try
        {
            proxy.Start();

            // correct credentials
            using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.EndPoint!.Port);
                using var s = new NetworkStream(client, ownsSocket: false);
                await s.WriteAsync(new byte[] { 0x05, 0x02, 0x00, 0x02 });
                var m = new byte[2];
                Assert.Equal(2, await ReadExactly(s, m));
                Assert.Equal(0x02, m[1]);

                var user = "ali"u8.ToArray();
                var pass = "secret123"u8.ToArray();
                await s.WriteAsync(new byte[] { 0x01, (byte)user.Length });
                await s.WriteAsync(user);
                await s.WriteAsync(new byte[] { (byte)pass.Length });
                await s.WriteAsync(pass);
                var a = new byte[2];
                Assert.Equal(2, await ReadExactly(s, a));
                Assert.Equal(0x00, a[1]); // success

                await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, (byte)(echoPort >> 8), (byte)(echoPort & 0xFF) });
                var c = new byte[10];
                Assert.Equal(10, await ReadExactly(s, c));
                Assert.Equal(0x00, c[1]);

                var msg = "over userpass"u8.ToArray();
                await s.WriteAsync(msg);
                var back = new byte[msg.Length];
                Assert.Equal(msg.Length, await ReadExactly(s, back));
                Assert.Equal(msg, back);
            }

            // wrong credentials
            using (var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp))
            {
                await client.ConnectAsync(IPAddress.Loopback, proxy.EndPoint!.Port);
                using var s = new NetworkStream(client, ownsSocket: false);
                await s.WriteAsync(new byte[] { 0x05, 0x01, 0x02 });
                var m = new byte[2];
                Assert.Equal(2, await ReadExactly(s, m));
                await s.WriteAsync(new byte[] { 0x01, 3 });
                await s.WriteAsync("bad"u8.ToArray());
                await s.WriteAsync(new byte[] { 3 });
                await s.WriteAsync("pwd"u8.ToArray());
                var a = new byte[2];
                Assert.Equal(2, await ReadExactly(s, a));
                Assert.Equal(0x01, a[1]); // failure
            }
        }
        finally
        {
            await proxy.DisposeAsync();
            echo.Dispose();
        }
    }

    [Fact]
    public async Task NonSocks5Client_DroppedGracefully()
    {
        var (echo, echoPort) = await StartEchoAsync();
        var proxy = StartProxy(FreePort(), Options(FreePort()), DialEcho(echoPort));
        try
        {
            proxy.Start();
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, proxy.EndPoint!.Port);
            using var s = new NetworkStream(client, ownsSocket: false);
            await s.WriteAsync("GET / HTTP/1.1\r\n\r\n"u8.ToArray());
            var buf = new byte[16];
            var n = 0;
            try { n = await s.ReadAsync(buf); } catch { }
            // Either closed (0) or a 0x05 0xFF method reply — must never be an HTTP response.
            if (n == 2) Assert.Equal(0xFF, buf[1]);
            else Assert.Equal(0, n);
        }
        finally
        {
            await proxy.DisposeAsync();
            echo.Dispose();
        }
    }

    [Fact]
    public async Task Concurrency_MultiplePipes_Work()
    {
        var (echo, echoPort) = await StartEchoAsync();
        var proxy = StartProxy(FreePort(), Options(FreePort()), DialEcho(echoPort));
        try
        {
            proxy.Start();
            var tasks = Enumerable.Range(0, 8).Select(async i =>
            {
                using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                await client.ConnectAsync(IPAddress.Loopback, proxy.EndPoint!.Port);
                using var s = new NetworkStream(client, ownsSocket: false);
                await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
                var m = new byte[2];
                Assert.Equal(2, await ReadExactly(s, m));
                await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00, 0x01, 127, 0, 0, 1, (byte)(echoPort >> 8), (byte)(echoPort & 0xFF) });
                var c = new byte[10];
                Assert.Equal(10, await ReadExactly(s, c));
                var msg = Encoding.UTF8.GetBytes("client-" + i);
                await s.WriteAsync(msg);
                var back = new byte[msg.Length];
                Assert.Equal(msg.Length, await ReadExactly(s, back));
                Assert.Equal(msg, back);
            });
            await Task.WhenAll(tasks);
        }
        finally
        {
            await proxy.DisposeAsync();
            echo.Dispose();
        }
    }

    [Fact]
    public async Task HandlerFailure_ReportsSocksError()
    {
        var proxy = StartProxy(FreePort(), Options(FreePort()),
            (_, _, _) => throw new HAZEM.Tunnel.Core.TunnelException(
                TunnelErrorCategory.ConnectionRefused, "refused by tunnel"));
        try
        {
            proxy.Start();
            using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            await client.ConnectAsync(IPAddress.Loopback, proxy.EndPoint!.Port);
            using var s = new NetworkStream(client, ownsSocket: false);
            await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00 });
            var m = new byte[2];
            Assert.Equal(2, await ReadExactly(s, m));
            await s.WriteAsync(new byte[] { 0x05, 0x01, 0x00, 0x01, 10, 0, 0, 1, 0x00, 0x50 });
            var c = new byte[10];
            Assert.Equal(10, await ReadExactly(s, c));
            Assert.Equal(0x05, c[0]);
            Assert.Equal(0x05, c[1]); // connection refused code
        }
        finally
        {
            await proxy.DisposeAsync();
        }
    }

    [Fact]
    public async Task StopAsync_StopsListener()
    {
        var (echo, echoPort) = await StartEchoAsync();
        var proxy = StartProxy(FreePort(), Options(FreePort()), DialEcho(echoPort));
        proxy.Start();
        var port = proxy.EndPoint!.Port;
        await proxy.StopAsync();
        await Task.Delay(100);
        using var client = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        await Assert.ThrowsAnyAsync<SocketException>(() => client.ConnectAsync(IPAddress.Loopback, port));
        echo.Dispose();
    }

    private static async Task<int> ReadExactly(Stream s, byte[] buffer)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await s.ReadAsync(buffer.AsMemory(read));
            if (n == 0) break;
            read += n;
        }
        return read;
    }
}
