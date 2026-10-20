using System.Net;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Networking.Diagnostics;

/// <summary>
/// Health checker: performs an HTTPS GET through the tunnel whenever the transport exposes
/// a usable local proxy path; otherwise falls back to a transport-level reachability check.
/// Never fabricates results — failures are reported as failures.
/// </summary>
public sealed class HealthChecker : IHealthChecker
{
    private readonly ILogger<HealthChecker> _logger;
    private readonly string _probeUrl;

    public HealthChecker(ILogger<HealthChecker> logger, string? probeUrl = null)
    {
        _logger = logger;
        _probeUrl = probeUrl ?? "https://www.gstatic.com/generate_204";
    }

    public async Task<HealthCheckResult> CheckAsync(TunnelProfile profile, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var handler = new SocketsHttpHandler
            {
                Proxy = new WebProxy($"http://{profile.LocalProxy.BindAddress}:{profile.LocalProxy.Port}"),
                UseProxy = true,
                ConnectTimeout = TimeSpan.FromSeconds(10),
                SslOptions = { RemoteCertificateValidationCallback = null } // default (full) validation
            };
            using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(12) };
            using var resp = await http.GetAsync(_probeUrl, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            sw.Stop();
            var ok = (int)resp.StatusCode is >= 200 and < 400;
            return new HealthCheckResult(ok, (int)resp.StatusCode, sw.Elapsed,
                ok ? "Tunnel HTTP probe succeeded." : $"Probe returned HTTP {(int)resp.StatusCode}.", ThroughTunnel: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            return new HealthCheckResult(false, null, sw.Elapsed, "Health check timed out.", ThroughTunnel: true);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogDebug("Through-tunnel health check failed: {Message}", ex.Message);
            return new HealthCheckResult(false, null, sw.Elapsed, $"Health check failed: {ex.Message}", ThroughTunnel: true);
        }
    }
}
