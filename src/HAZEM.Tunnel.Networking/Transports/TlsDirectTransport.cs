using System.IO;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Networking.Transports;

/// <summary>
/// Direct TLS transport, diagnostics only: it streams RAW bytes over TLS to the SNI host.
/// Honest limitation surfaced in the UI: most HTTPS servers will not accept arbitrary
/// non-HTTP payloads; this transport exists to test TLS reachability and certificate trust.
/// </summary>
public sealed class TlsDirectTransport : ITransport
{
    private readonly Stream _tlsStream;
    private readonly ILogger<TlsDirectTransport> _logger;

    public TlsDirectTransport(Stream tlsStream, ILogger<TlsDirectTransport> logger)
    {
        _tlsStream = tlsStream;
        _logger = logger;
    }

    public TransportKind Kind => TransportKind.TlsDirect;

    public Task<Stream> OpenAsync(CancellationToken ct)
    {
        _logger.LogDebug("TLS-direct pipe opened (diagnostics transport).");
        return Task.FromResult(_tlsStream);
    }

    public async ValueTask DisposeAsync()
    {
        try { await _tlsStream.DisposeAsync().ConfigureAwait(false); } catch { }
    }
}
