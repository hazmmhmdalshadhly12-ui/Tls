namespace HAZEM.Tunnel.Core.Retry;

/// <summary>
/// Bounded retry with exponential backoff + jitter. Cancellation is honored between attempts.
/// Uses TimeProvider for testability. Never retries OperationCanceledException.
/// </summary>
public sealed class RetryPolicy
{
    private readonly int _maxAttempts;
    private readonly TimeSpan _baseDelay;
    private readonly TimeSpan _maxDelay;
    private readonly double _jitterRatio;
    private readonly TimeProvider _timeProvider;
    private readonly Random _rng = Random.Shared;

    public RetryPolicy(int maxAttempts = 5, int baseDelayMs = 1000, int maxDelayMs = 30_000,
        double jitterRatio = 0.2, TimeProvider? timeProvider = null)
    {
        if (maxAttempts < 1) throw new ArgumentOutOfRangeException(nameof(maxAttempts));
        if (jitterRatio is < 0 or > 1) throw new ArgumentOutOfRangeException(nameof(jitterRatio));
        _maxAttempts = maxAttempts;
        _baseDelay = TimeSpan.FromMilliseconds(baseDelayMs);
        _maxDelay = TimeSpan.FromMilliseconds(maxDelayMs);
        _jitterRatio = jitterRatio;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static RetryPolicy FromOptions(Models.RetryOptions o, TimeProvider? tp = null)
        => new(o.MaxAttempts, o.BaseDelayMs, o.MaxDelayMs, o.JitterRatio, tp);

    public int MaxAttempts => _maxAttempts;

    /// <summary>Computes the delay before attempt N (1-based) deterministically except for jitter.</summary>
    public TimeSpan DelayForAttempt(int attempt)
    {
        if (attempt < 1) attempt = 1;
        var exp = Math.Min(attempt - 1, 30);
        var delayMs = _baseDelay.TotalMilliseconds * Math.Pow(2, exp);
        if (delayMs > _maxDelay.TotalMilliseconds) delayMs = _maxDelay.TotalMilliseconds;
        var jitter = delayMs * _jitterRatio * (_rng.NextDouble() * 2 - 1); // ±jitterRatio
        delayMs = Math.Max(0, delayMs + jitter);
        return TimeSpan.FromMilliseconds(Math.Min(delayMs, _maxDelay.TotalMilliseconds * 1.5));
    }

    /// <summary>Runs <paramref name="action"/> with retries. Rethrows the last exception when attempts are exhausted.</summary>
    public async Task ExecuteAsync(Func<CancellationToken, Task> action, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await action(ct).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                if (attempt >= _maxAttempts) throw;
                await WaitAsync(DelayForAttempt(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Runs <paramref name="func"/> with retries and returns its result.</summary>
    public async Task<T> ExecuteAsync<T>(Func<CancellationToken, Task<T>> func, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return await func(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                if (ex is OperationCanceledException) throw;
                if (attempt >= _maxAttempts) throw;
                await WaitAsync(DelayForAttempt(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    private async Task WaitAsync(TimeSpan delay, CancellationToken ct)
    {
        try
        {
            await Task.Delay(delay, _timeProvider, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // User cancelled during backoff — surface cancellation, not the original failure.
            throw;
        }
    }
}
