using HAZEM.Tunnel.Core.Retry;
using Xunit;

namespace HAZEM.Tunnel.Core.Tests;

public class RetryPolicyTests
{
    [Fact]
    public async Task SuccessFirstTry_NoDelays()
    {
        var policy = new RetryPolicy(maxAttempts: 3, baseDelayMs: 1, maxDelayMs: 2);
        var calls = 0;
        await policy.ExecuteAsync(_ => { calls++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task RetriesUntilSuccess_Bounded()
    {
        var policy = new RetryPolicy(maxAttempts: 4, baseDelayMs: 1, maxDelayMs: 2);
        var calls = 0;
        await policy.ExecuteAsync(_ =>
        {
            calls++;
            return calls < 3 ? throw new IOException("boom") : Task.CompletedTask;
        }, CancellationToken.None);
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task ThrowsAfterMaxAttempts()
    {
        var policy = new RetryPolicy(maxAttempts: 3, baseDelayMs: 1, maxDelayMs: 2);
        var calls = 0;
        await Assert.ThrowsAsync<IOException>(() =>
            policy.ExecuteAsync(_ => { calls++; throw new IOException("always"); }, CancellationToken.None));
        Assert.Equal(3, calls);
    }

    [Fact]
    public async Task CancellationBetweenAttempts_StopsRetrying()
    {
        var policy = new RetryPolicy(maxAttempts: 10, baseDelayMs: 1, maxDelayMs: 2);
        using var cts = new CancellationTokenSource();
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.ExecuteAsync(_ =>
            {
                calls++;
                cts.Cancel();
                throw new IOException("fail");
            }, cts.Token));
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task DoesNotSwallowCancellationInsideAction()
    {
        var policy = new RetryPolicy(maxAttempts: 5, baseDelayMs: 1, maxDelayMs: 2);
        using var cts = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            policy.ExecuteAsync(ct => { cts.Cancel(); ct.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cts.Token));
    }

    [Fact]
    public void DelayGrows_Exponentially_AndCaps()
    {
        var policy = new RetryPolicy(maxAttempts: 20, baseDelayMs: 100, maxDelayMs: 5_000, jitterRatio: 0);
        var d1 = policy.DelayForAttempt(1);
        var d2 = policy.DelayForAttempt(2);
        var d3 = policy.DelayForAttempt(3);
        var d9 = policy.DelayForAttempt(9);
        var d30 = policy.DelayForAttempt(30);
        Assert.Equal(100, d1.TotalMilliseconds);
        Assert.True(d2 > d1);
        Assert.True(d3 > d2);
        Assert.Equal(5000, d9.TotalMilliseconds);
        Assert.Equal(5000, d30.TotalMilliseconds);
    }

    [Fact]
    public void Jitter_StaysWithinRatio()
    {
        var policy = new RetryPolicy(maxAttempts: 5, baseDelayMs: 1000, maxDelayMs: 60_000, jitterRatio: 0.2);
        for (var i = 0; i < 50; i++)
        {
            var d = policy.DelayForAttempt(1).TotalMilliseconds;
            Assert.InRange(d, 800, 1200);
        }
    }

    [Fact]
    public async Task FuncOverload_ReturnsValue()
    {
        var policy = new RetryPolicy(3, 1, 2);
        var calls = 0;
        var result = await policy.ExecuteAsync(_ =>
        {
            calls++;
            return calls < 2 ? throw new InvalidOperationException() : Task.FromResult(42);
        }, CancellationToken.None);
        Assert.Equal(42, result);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void InvalidArguments_Rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RetryPolicy(3, jitterRatio: 2));
    }

    [Fact]
    public void FromOptions_MapsFields()
    {
        var policy = RetryPolicy.FromOptions(new HAZEM.Tunnel.Core.Models.RetryOptions
        {
            MaxAttempts = 7,
            BaseDelayMs = 250,
            MaxDelayMs = 5_000,
            JitterRatio = 0
        });
        Assert.Equal(7, policy.MaxAttempts);
        Assert.Equal(250, policy.DelayForAttempt(1).TotalMilliseconds);
    }
}
