using HAZEM.Tunnel.Core.Statistics;
using Xunit;

namespace HAZEM.Tunnel.Core.Tests;

public class StatisticsTrackerTests
{
    [Fact]
    public void Counters_AreThreadSafe_UnderConcurrentUpdates()
    {
        var tracker = new StatisticsTracker(samplingInterval: null);
        const int threads = 8, ops = 10_000;
        Parallel.For(0, threads, _ =>
        {
            for (var i = 0; i < ops; i++)
            {
                tracker.AddBytesSent(1);
                tracker.AddBytesReceived(2);
                tracker.OnConnectionOpened();
                tracker.OnConnectionClosed();
            }
        });
        var snap = tracker.GetSnapshot();
        Assert.Equal(threads * ops, snap.TotalBytesSent);
        Assert.Equal(threads * ops * 2, snap.TotalBytesReceived);
        Assert.Equal(threads * ops, snap.TotalConnections);
        Assert.Equal(0, snap.ActiveConnections);
    }

    [Fact]
    public void TakeSample_ReportsDeltaSinceLastCall()
    {
        var tracker = new StatisticsTracker(samplingInterval: null);
        tracker.AddBytesSent(100);
        var s1 = tracker.TakeSample();
        Assert.Equal(100, s1.SendBytesPerSecond);
        tracker.AddBytesSent(50);
        var s2 = tracker.TakeSample();
        Assert.Equal(50, s2.SendBytesPerSecond);
    }

    [Fact]
    public void Samples_Bounded_KeepRecentOnly()
    {
        var tracker = new StatisticsTracker(samplingInterval: null);
        for (var i = 0; i < 700; i++)
        {
            tracker.AddBytesSent(1);
            tracker.TakeSample();
        }
        Assert.Equal(600, tracker.GetRecentSamples().Count);
    }

    [Fact]
    public void MarkConnected_SetsUptime_OnlyOnceUntilDisconnect()
    {
        var tracker = new StatisticsTracker(samplingInterval: null);
        tracker.MarkConnected();
        var first = tracker.GetSnapshot().ConnectedSince;
        tracker.MarkConnected();
        var second = tracker.GetSnapshot().ConnectedSince;
        Assert.Equal(first, second);
        tracker.MarkDisconnected();
        Assert.Null(tracker.GetSnapshot().ConnectedSince);
    }

    [Fact]
    public void Reconnects_Counted()
    {
        var tracker = new StatisticsTracker(samplingInterval: null);
        tracker.OnReconnect();
        tracker.OnReconnect();
        Assert.Equal(2, tracker.GetSnapshot().ReconnectCount);
    }

    [Fact]
    public void SampleTaken_EventFires()
    {
        var tracker = new StatisticsTracker(samplingInterval: null);
        var fired = 0;
        tracker.SampleTaken += _ => fired++;
        tracker.TakeSample();
        Assert.Equal(1, fired);
    }
}
