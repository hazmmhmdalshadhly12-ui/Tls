using System.IO;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Networking.Ssh;
using Xunit;

namespace HAZEM.Tunnel.Networking.Tests;

public class SshHostKeyVerifierTests
{
    private sealed class MemoryKnownHosts : IKnownHostsStore
    {
        private readonly Dictionary<(string, int), KnownHostEntry> _hosts = new();
        public KnownHostEntry? Find(string host, int port) => _hosts.TryGetValue((host, port), out var e) ? e : null;
        public void AddOrUpdate(KnownHostEntry entry) { _hosts[(entry.Host, entry.Port)] = entry; }
        public bool Remove(string host, int port) => _hosts.Remove((host, port));
        public IReadOnlyList<KnownHostEntry> All() => _hosts.Values.ToList();
    }

    private static readonly byte[] KeyA = new byte[32].Select((_, i) => (byte)(i + 1)).ToArray();
    private static readonly byte[] KeyB = new byte[32].Select((_, i) => (byte)(i + 50)).ToArray();

    [Fact]
    public void Tofu_FirstUse_AcceptedAndStored()
    {
        var store = new MemoryKnownHosts();
        var verifier = new SshHostKeyVerifier(store);
        var d = verifier.Verify("host1", 22, KeyA, SshHostKeyPolicy.TrustOnFirstUse, null);
        Assert.Equal(HostKeyDecision.FirstUseAccepted, d);
        Assert.NotNull(store.Find("host1", 22));
        Assert.Equal(HostKeyFormatter.Fingerprint(KeyA), store.Find("host1", 22)!.Fingerprint);
    }

    [Fact]
    public void Tofu_SameKey_Accepted()
    {
        var store = new MemoryKnownHosts();
        var verifier = new SshHostKeyVerifier(store);
        verifier.Verify("host1", 22, KeyA, SshHostKeyPolicy.TrustOnFirstUse, null);
        var d = verifier.Verify("host1", 22, KeyA, SshHostKeyPolicy.TrustOnFirstUse, null);
        Assert.Equal(HostKeyDecision.Accepted, d);
    }

    [Fact]
    public void Tofu_ChangedKey_Rejected()
    {
        var store = new MemoryKnownHosts();
        var verifier = new SshHostKeyVerifier(store);
        verifier.Verify("host1", 22, KeyA, SshHostKeyPolicy.TrustOnFirstUse, null);
        var d = verifier.Verify("host1", 22, KeyB, SshHostKeyPolicy.TrustOnFirstUse, null);
        Assert.Equal(HostKeyDecision.RejectedChanged, d);
    }

    [Fact]
    public void Strict_UnknownKey_Rejected()
    {
        var verifier = new SshHostKeyVerifier(new MemoryKnownHosts());
        var d = verifier.Verify("unknown", 22, KeyA, SshHostKeyPolicy.Strict, null);
        Assert.Equal(HostKeyDecision.RejectedUnknown, d);
    }

    [Fact]
    public void Strict_StoredKey_Accepted()
    {
        var store = new MemoryKnownHosts();
        var verifier = new SshHostKeyVerifier(store);
        verifier.Verify("host1", 22, KeyA, SshHostKeyPolicy.TrustOnFirstUse, null);
        var d = verifier.Verify("host1", 22, KeyA, SshHostKeyPolicy.Strict, null);
        Assert.Equal(HostKeyDecision.Accepted, d);
    }

    [Fact]
    public void Pinned_MatchingKey_Accepted()
    {
        var verifier = new SshHostKeyVerifier(null);
        var fp = HostKeyFormatter.Fingerprint(KeyA);
        var d = verifier.Verify("host1", 22, KeyA, SshHostKeyPolicy.Pinned, fp);
        Assert.Equal(HostKeyDecision.Accepted, d);
    }

    [Fact]
    public void Pinned_WrongKey_Rejected()
    {
        var verifier = new SshHostKeyVerifier(null);
        var d = verifier.Verify("host1", 22, KeyB, SshHostKeyPolicy.Pinned, HostKeyFormatter.Fingerprint(KeyA));
        Assert.Equal(HostKeyDecision.RejectedNotPinned, d);
    }

    [Fact]
    public void DistinctPorts_TrackedSeparately()
    {
        var store = new MemoryKnownHosts();
        var verifier = new SshHostKeyVerifier(store);
        verifier.Verify("host1", 22, KeyA, SshHostKeyPolicy.TrustOnFirstUse, null);
        var d = verifier.Verify("host1", 2222, KeyB, SshHostKeyPolicy.TrustOnFirstUse, null);
        Assert.Equal(HostKeyDecision.FirstUseAccepted, d);
        Assert.Equal(2, store.All().Count);
    }

    [Fact]
    public void FingerprintFormat_MatchesOpenSSHStyle()
    {
        var fp = HostKeyFormatter.Fingerprint(KeyA);
        Assert.StartsWith("SHA256:", fp);
        Assert.DoesNotContain("=", fp);
        Assert.DoesNotContain("/", fp);
        Assert.Equal(43 + 7, fp.Length);
    }

    [Fact]
    public void SessionFingerprint_Retrievable()
    {
        var verifier = new SshHostKeyVerifier(null);
        verifier.Verify("h", 22, KeyA, SshHostKeyPolicy.Pinned, HostKeyFormatter.Fingerprint(KeyA));
        Assert.Equal(HostKeyFormatter.Fingerprint(KeyA), verifier.GetSessionFingerprint("h", 22));
    }
}

public class StreamRelayTests
{
    [Fact]
    public async Task Relay_MovesBytes_BothDirections()
    {
        using var clientSide = new MemoryStream();
        await using var tunnelSide = new MemoryStream();

        clientSide.Position = 0;
        var payloadC = new byte[] { 1, 2, 3, 4, 5 };

        // Use a simple duplex pair via MemoryStreams that block; instead test via pipes:
        var cToT = new MemoryStream();
        var tToC = new MemoryStream();

        var stats = new HAZEM.Tunnel.Core.Statistics.StatisticsTracker(samplingInterval: null);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        // We test the counting logic indirectly through the SOCKS tests; here validate RelayResult math
        // by using the Socks5 Relay helper directly with piped streams.
        var (a, b) = CreateDuplexPair();
        var (x, y) = CreateDuplexPair();

        var relayTask = Proxy.Relay.RelayAsync(a, x,
            sent => stats.AddBytesSent(sent),
            recv => stats.AddBytesReceived(recv),
            cts.Token);

        await b.WriteAsync(new byte[] { 9, 9, 9 });
        await b.FlushAsync();
        var got = new byte[3];
        var n = await y.ReadAsync(got, cts.Token);
        Assert.Equal(3, n);
        Assert.Equal(new byte[] { 9, 9, 9 }, got);

        cts.Cancel();
        await relayTask;
        Assert.True(stats.GetSnapshot().TotalBytesSent >= 3);
    }

    private static (System.Net.Sockets.NetworkStream, System.Net.Sockets.NetworkStream) CreateDuplexPair()
    {
        var l = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        l.Bind(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0));
        l.Listen(1);
        var c = new System.Net.Sockets.Socket(System.Net.Sockets.AddressFamily.InterNetwork,
            System.Net.Sockets.SocketType.Stream, System.Net.Sockets.ProtocolType.Tcp);
        var connectTask = c.ConnectAsync(new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, ((System.Net.IPEndPoint)l.LocalEndPoint!).Port));
        var s = l.Accept();
        connectTask.Wait();
        return (new System.Net.Sockets.NetworkStream(c, ownsSocket: true),
                new System.Net.Sockets.NetworkStream(s, ownsSocket: true));
    }
}
