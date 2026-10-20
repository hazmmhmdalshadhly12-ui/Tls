using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Networking.Tls;

/// <summary>
/// DNS → TCP → TLS with explicit SNI. The SNI (TargetHost) and the HTTP Host are
/// different concepts on purpose: this connector only handles the TLS-layer identity.
/// Certificate validation can never be disabled.
/// </summary>
public sealed class TlsConnector : ITlsConnector
{
    private readonly ILogger<TlsConnector> _logger;

    public TlsConnector(ILogger<TlsConnector> logger) => _logger = logger;

    public async Task<(Stream Stream, CertificateInfo Info)> ConnectAsync(TlsOptions options, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(options);
        var sni = string.IsNullOrWhiteSpace(options.Sni) ? options.Host : options.Sni;

        _logger.LogInformation("TLS connect: host={Host}:{Port} sni={Sni} validate={Validate}",
            options.Host, options.Port, sni, options.ValidateCertificates);

        // 1. DNS
        IPAddress[] addresses;
        try
        {
            addresses = await Dns.GetHostAddressesAsync(options.Host, ct).ConfigureAwait(false);
        }
        catch (SocketException ex)
        {
            throw new TunnelException(TunnelErrorCategory.Dns, $"DNS lookup failed for '{options.Host}': {ex.Message}", ex);
        }
        if (addresses.Length == 0)
            throw new TunnelException(TunnelErrorCategory.Dns, $"DNS returned no addresses for '{options.Host}'.");

        // 2. TCP (try all addresses, honor cancellation)
        Exception? last = null;
        Socket? socket = null;
        foreach (var addr in addresses)
        {
            ct.ThrowIfCancellationRequested();
            var s = new Socket(addr.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(options.HandshakeTimeout);
                await s.ConnectAsync(addr, options.Port, timeoutCts.Token).ConfigureAwait(false);
                socket = s;
                break;
            }
            catch (Exception ex) when (ex is SocketException or OperationCanceledException && ex is not OperationCanceledException)
            {
                last = ex;
                s.Dispose();
            }
            catch
            {
                s.Dispose();
                throw;
            }
        }
        if (socket is null)
            throw new TunnelException(TunnelErrorCategory.NetworkUnreachable,
                $"TCP connect to {options.Host}:{options.Port} failed: {last?.Message ?? "all addresses failed."}", last);

        // 3. TLS with SNI = sni; validation via CertificateValidator
        var ssl = new SslStream(new NetworkStream(socket, ownsSocket: true), leaveInnerStreamOpen: false);
        try
        {
            using var hsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            hsCts.CancelAfter(options.HandshakeTimeout);

            var validator = new CertificateValidator(sni, options.TrustedRootPem);
            SslClientAuthenticationOptions auth = new()
            {
                TargetHost = sni, // this is THE SNI field of ClientHello
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = null,
                RemoteCertificateValidationCallback = (_, cert, _, errors) =>
                {
                    if (cert is null) return false;
                    var (chainValid, hostValid) = validator.Validate(null, new X509Certificate2(cert));
                    // System errors are additionally honored (revocation etc.)
                    return chainValid && hostValid && errors is SslPolicyErrors.None or SslPolicyErrors.RemoteCertificateChainErrors;
                }
            };

            await ssl.AuthenticateAsClientAsync(auth, hsCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw new TunnelException(TunnelErrorCategory.Timeout,
                $"TLS handshake to {sni} timed out after {options.HandshakeTimeout.TotalSeconds:0}s.");
        }
        catch (AuthenticationException ex)
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            var category = ex.Message.Contains("remote certificate", StringComparison.OrdinalIgnoreCase)
                ? TunnelErrorCategory.Certificate
                : TunnelErrorCategory.TlsHandshake;
            throw new TunnelException(category, $"TLS handshake with {sni} failed: {ex.Message}", ex);
        }
        catch
        {
            await ssl.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        var tlsCert = ssl.RemoteCertificate;
        var info = DescribeCertificate(tlsCert, sni, chainValidated: true);
        _logger.LogInformation("TLS established with {Sni}: subject={Subject} valid until {NotAfter}",
            sni, info.Subject, info.NotAfter.LocalDateTime);

        return (ssl, info);
    }

    public static CertificateInfo DescribeCertificate(X509Certificate? raw, string sni, bool chainValidated)
    {
        if (raw is null)
            throw new TunnelException(TunnelErrorCategory.Certificate, "Server did not present a TLS certificate.");
        using var cert = raw is X509Certificate2 c2 ? c2 : new X509Certificate2(raw);
        var names = CertificateHostnameValidator.GetDnsNames(cert).ToList();
        var hostValid = CertificateHostnameValidator.Matches(cert, sni);
        return new CertificateInfo(
            cert.Subject,
            cert.Issuer,
            cert.NotBefore.ToUniversalTime(),
            cert.NotAfter.ToUniversalTime(),
            cert.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256),
            names,
            chainValidated,
            hostValid);
    }
}
