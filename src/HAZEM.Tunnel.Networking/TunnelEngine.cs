using System.IO;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Retry;
using HAZEM.Tunnel.Core.Statistics;
using HAZEM.Tunnel.Networking.Proxy;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Networking;

/// <summary>
/// Staged tunnel engine. HARD RULE: the state becomes Connected ONLY after a real
/// through-tunnel health check succeeds. Reconnects are bounded; a periodic health loop
/// restarts the tunnel after repeated failures. All counters are real.
/// </summary>
public sealed class TunnelEngine : ITunnelEngine
{
    public const int HealthFailuresBeforeRestart = 3;

    private readonly TransportFactory _factory;
    private readonly IHealthChecker _health;
    private readonly ILogger<TunnelEngine> _logger;
    private readonly Func<string, string, bool>? _proxyCredentialChecker;

    private readonly object _gate = new();
    private CancellationTokenSource? _runCts;
    private Task? _runTask;
    private TunnelProfile? _profile;
    private ConnectionState _state = ConnectionState.Disconnected;
    private ConnectionStage _stage = ConnectionStage.Idle;
    private CertificateInfo? _lastCert;
    private Socks5ProxyServer? _proxy;
    private ITransport? _activeTransport;

    public StatisticsTracker Statistics { get; } = new();
    public CertificateInfo? LastCertificate { get { lock (_gate) return _lastCert; } }

    public ConnectionState State
    {
        get { lock (_gate) return _state; }
        private set
        {
            ConnectionState old;
            lock (_gate)
            {
                old = _state;
                _state = value;
            }
            if (old != value)
            {
                _logger.LogInformation("Tunnel state: {Old} -> {New}", old, value);
                StateChanged?.Invoke(value);
            }
        }
    }

    public ConnectionStage Stage
    {
        get { lock (_gate) return _stage; }
        private set
        {
            ConnectionStage old;
            lock (_gate)
            {
                old = _stage;
                _stage = value;
            }
            if (old != value) StageChanged?.Invoke(value);
        }
    }

    public event Action<ConnectionState>? StateChanged;
    public event Action<ConnectionStage>? StageChanged;
    public event Action<string, string>? EventLogged;
    public event Action<HealthCheckResult>? HealthChecked;

    public TunnelEngine(TransportFactory factory, IHealthChecker health, ILogger<TunnelEngine> logger,
        Func<string, string, bool>? proxyCredentialChecker = null)
    {
        _factory = factory;
        _health = health;
        _logger = logger;
        _proxyCredentialChecker = proxyCredentialChecker;
    }

    private void Log(string level, string message)
    {
        EventLogged?.Invoke(level, message);
        switch (level)
        {
            case "WARN": _logger.LogWarning("{Message}", message); break;
            case "ERROR": _logger.LogError("{Message}", message); break;
            default: _logger.LogInformation("{Message}", message); break;
        }
    }

    public async Task ConnectAsync(TunnelProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        lock (_gate)
        {
            if (_state is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Reconnecting)
                throw new TunnelException(TunnelErrorCategory.Configuration,
                    "Tunnel is already active. Disconnect first.");
            _profile = profile.Clone();
        }

        Statistics.MarkDisconnected();
        State = ConnectionState.Connecting;
        Stage = ConnectionStage.Idle;
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _runTask = Task.Run(() => RunLoopAsync(_runCts.Token));
        await Task.CompletedTask.ConfigureAwait(false);
    }

    public async Task DisconnectAsync()
    {
        _runCts?.Cancel();
        if (_runTask is not null)
        {
            try { await _runTask.WaitAsync(TimeSpan.FromSeconds(8)).ConfigureAwait(false); }
            catch (TimeoutException) { Log("WARN", "Tunnel stop timed out; forcing cleanup."); }
            catch (OperationCanceledException) { }
        }
        await CleanupAsync().ConfigureAwait(false);
        State = ConnectionState.Disconnected;
        Stage = ConnectionStage.Idle;
        Statistics.MarkDisconnected();
        Log("INFO", "Tunnel disconnected.");
    }

    private async Task CleanupAsync()
    {
        ITransport? transport;
        lock (_gate)
        {
            transport = _activeTransport;
            _activeTransport = null;
        }
        if (transport is not null)
        {
            try { await transport.DisposeAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
        }
        Socks5ProxyServer? proxy;
        lock (_gate)
        {
            proxy = _proxy;
            _proxy = null;
        }
        if (proxy is not null)
        {
            try { await proxy.StopAsync().ConfigureAwait(false); }
            catch { /* best effort */ }
            await proxy.DisposeAsync().ConfigureAwait(false);
            _logger.LogInformation("SOCKS5 proxy stopped.");
        }
    }

    // ---- main loop: connect with bounded retries, then supervise health ----
    private async Task RunLoopAsync(CancellationToken ct)
    {
        TunnelProfile profile;
        lock (_gate) profile = _profile!;
        var retry = RetryPolicy.FromOptions(profile.Retry);

        try
        {
            // Connect phase (bounded retries with backoff)
            var attempt = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                attempt++;
                try
                {
                    await EstablishAsync(profile, ct).ConfigureAwait(false);
                    break;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    Log("ERROR", $"Attempt {attempt}/{retry.MaxAttempts} failed: {Describe(ex)}");
                    if (attempt >= retry.MaxAttempts)
                    {
                        State = ConnectionState.Failed;
                        Stage = ConnectionStage.Failed;
                        Log("ERROR", $"Giving up after {attempt} attempts. Fix the profile or the network, then reconnect.");
                        return;
                    }
                    State = ConnectionState.Reconnecting;
                    var delay = retry.DelayForAttempt(attempt);
                    Log("INFO", $"Retrying in {delay.TotalSeconds:0.#}s…");
                    await Task.Delay(delay, ct).ConfigureAwait(false);
                    Statistics.OnReconnect();
                }
            }

            // Supervision phase: periodic health checks; restart after consecutive failures.
            var consecutiveFailures = 0;
            while (!ct.IsCancellationRequested)
            {
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }

                var result = await _health.CheckAsync(profile, ct).ConfigureAwait(false);
                HealthChecked?.Invoke(result);
                if (result.Success)
                {
                    consecutiveFailures = 0;
                    Log("INFO", $"Health check OK ({result.Latency.TotalMilliseconds:0} ms).");
                    continue;
                }

                consecutiveFailures++;
                Log("WARN", $"Health check failed ({consecutiveFailures}/{HealthFailuresBeforeRestart}): {result.Detail}");
                if (consecutiveFailures < HealthFailuresBeforeRestart) continue;

                Log("WARN", $"{HealthFailuresBeforeRestart} consecutive health failures — restarting the tunnel.");
                Statistics.OnReconnect();
                State = ConnectionState.Reconnecting;
                await CleanupAsync().ConfigureAwait(false);
                break;
            }

            // If we left supervision due to failures, reconnect from scratch (bounded by the same retry policy).
            if (!ct.IsCancellationRequested && State == ConnectionState.Reconnecting)
            {
                await RunLoopAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Log("INFO", "Tunnel stopped by user.");
        }
        catch (Exception ex)
        {
            Log("ERROR", $"Unexpected engine error: {Describe(ex)}");
            State = ConnectionState.Failed;
            Stage = ConnectionStage.Failed;
        }
    }

    private async Task EstablishAsync(TunnelProfile profile, CancellationToken ct)
    {
        // Stage 1: local SOCKS5 proxy first (fast, local).
        Stage = ConnectionStage.StartingProxy;
        await StartProxyAsync(profile, ct).ConfigureAwait(false);

        // Stage 2: transport establishment (DNS/TCP/TLS/SSH/WS as applicable).
        Stage = profile.TransportKind switch
        {
            TransportKind.TlsDirect => ConnectionStage.EstablishingTls,
            TransportKind.Ssh => ConnectionStage.AuthenticatingSsh,
            TransportKind.TlsSsh => ConnectionStage.EstablishingTls,
            TransportKind.WebSocket => ConnectionStage.EstablishingTcp,
            _ => ConnectionStage.EstablishingTcp
        };
        var transport = await _factory.CreateAsync(profile, ct).ConfigureAwait(false);
        lock (_gate)
        {
            _activeTransport = transport;
            if (_factory.LastCertificate is { } cert) _lastCert = cert;
        }

        // Stage 3: real through-tunnel health check before allowing "Connected".
        Stage = ConnectionStage.HealthCheck;
        Log("INFO", "Transport established; running through-tunnel health check…");
        var health = await _health.CheckAsync(profile, ct).ConfigureAwait(false);
        HealthChecked?.Invoke(health);
        if (!health.Success)
        {
            await CleanupAsync().ConfigureAwait(false);
            throw new TunnelException(TunnelErrorCategory.HealthCheckFailed,
                $"Tunnel established but health check failed: {health.Detail}");
        }

        // ONLY NOW the UI may show Connected.
        Stage = ConnectionStage.Connected;
        State = ConnectionState.Connected;
        Statistics.MarkConnected();
        Log("INFO", $"CONNECTED via {profile.TransportKind}. Health check passed in {health.Latency.TotalMilliseconds:0} ms.");
    }

    // ---- local SOCKS5 plumbing ----
    private async Task StartProxyAsync(TunnelProfile profile, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_proxy is not null) return;
        }
        var proxy = new Socks5ProxyServer(
            profile.LocalProxy,
            async (host, port, token) =>
            {
                // Each SOCKS5 CONNECT opens a FRESH tunnel pipe through the active transport.
                ITransport transport;
                lock (_gate)
                {
                    transport = _activeTransport
                        ?? throw new TunnelException(TunnelErrorCategory.Unknown, "Tunnel transport is not ready.");
                }
                return await transport.OpenAsync(token).ConfigureAwait(false);
            },
            _proxyCredentialChecker,
            Statistics,
            _logger);
        proxy.Start();
        ct.ThrowIfCancellationRequested();
        lock (_gate) _proxy = proxy;
        Log("INFO", $"SOCKS5 proxy ready at {proxy.EndPoint} (only apps configured to use it are tunneled).");
    }

    private static string Describe(Exception ex) => ex is TunnelException te
        ? $"[{te.Category}] {te.Message}"
        : ex.Message;

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync().ConfigureAwait(false);
        Statistics.Dispose();
    }
}
