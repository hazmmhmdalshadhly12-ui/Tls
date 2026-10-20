using System.IO;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Statistics;

namespace HAZEM.Tunnel.Networking.Relay;

/// <summary>
/// Bidirectional counted pump between two streams. Every byte moved updates the
/// StatisticsTracker with real values — no synthetic numbers anywhere.
/// </summary>
public static class StreamRelay
{
    public const int BufferSize = 8192;

    public static async Task<RelayResult> RunAsync(Stream clientSide, Stream tunnelSide,
        StatisticsTracker? stats, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var cToT = PumpAsync(clientSide, tunnelSide, stats, forClient: true, linked.Token);
        var tToC = PumpAsync(tunnelSide, clientSide, stats, forClient: false, linked.Token);

        var first = await Task.WhenAny(cToT, tToC).ConfigureAwait(false);
        linked.Cancel();
        // Give the other direction a short grace period to finish naturally.
        var other = ReferenceEquals(first, cToT) ? tToC : cToT;
        try
        {
            await other.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException) { }

        var (clientToTunnel, tunnelToClient) = (await cToT.ConfigureAwait(false), await tToC.ConfigureAwait(false));
        return new RelayResult(clientToTunnel, tunnelToClient);
    }

    private static async Task<long> PumpAsync(Stream from, Stream to, StatisticsTracker? stats,
        bool forClient, CancellationToken ct)
    {
        var buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(BufferSize);
        long moved = 0;
        try
        {
            while (true)
            {
                int n;
                try
                {
                    n = await from.ReadAsync(buffer.AsMemory(0, BufferSize), ct).ConfigureAwait(false);
                }
                catch (System.IO.IOException) when (ct.IsCancellationRequested)
                {
                    break;
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                if (n <= 0) break;

                await to.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
                moved += n;
                if (stats is not null)
                {
                    if (forClient) stats.AddBytesReceived(n);
                    else stats.AddBytesSent(n);
                }
            }
        }
        catch (OperationCanceledException) { /* normal shutdown */ }
        catch (System.IO.IOException) { /* socket closed by peer */ }
        catch (ObjectDisposedException) { /* teardown */ }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
            try { to.Close(); } catch { }
        }
        return moved;
    }
}

public sealed record RelayResult(long BytesClientToTunnel, long BytesTunnelToClient);
