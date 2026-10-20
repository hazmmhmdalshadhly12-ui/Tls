using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Networking.Tls;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HAZEM.Tunnel.Networking.Tests;

/// <summary>Live TLS handshake tests against in-memory loopback servers with self-signed certs.</summary>
public class TlsConnectorTests
{
    /// <summary>Builds a DNS-only SAN extension by hand (rawData ctors only on this runtime).</summary>
    private static X509SubjectAlternativeNameExtension SanDns(string hostname)
    {
        var name = System.Text.Encoding.ASCII.GetBytes(hostname);
        var der = new byte[name.Length + 4];
        der[0] = 0x30; der[1] = (byte)(name.Length + 2);   // SEQUENCE
        der[2] = 0x82; der[3] = (byte)name.Length;          // [2] IMPLICIT IA5String (dNSName)
        Array.Copy(name, 0, der, 4, name.Length);
        return new X509SubjectAlternativeNameExtension(der, critical: true);
    }

    private static (X509Certificate2 Server, string Pem) MakeCert(string hostname)
    {
        using var rsa = RSA.Create(2048);
        var req = new CertificateRequest("CN=" + hostname, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, false));
        req.CertificateExtensions.Add(SanDns(hostname));
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        var exported = X509CertificateLoader.LoadPkcs12(cert.Export(X509ContentType.Pfx), null);
        return (exported, "-----BEGIN CERTIFICATE-----\n" +
               Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks) +
               "\n-----END CERTIFICATE-----\n");
    }

    private static int FreePort()
    {
        var l = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        l.Bind(new IPEndPoint(IPAddress.Loopback, 0));
        var p = ((IPEndPoint)l.LocalEndPoint!).Port;
        l.Dispose();
        return p;
    }

    private sealed class TlsServer : IAsyncDisposable
    {
        public required Socket Listener { get; init; }
        public required X509Certificate2 Certificate { get; init; }
        public int Port => ((IPEndPoint)Listener.LocalEndPoint!).Port;

        public static TlsServer Start(X509Certificate2 cert)
        {
            var listener = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            listener.Bind(new IPEndPoint(IPAddress.Loopback, 0));
            listener.Listen(4);
            var server = new TlsServer { Listener = listener, Certificate = cert };
            _ = Task.Run(server.AcceptLoop);
            return server;
        }

        private async Task AcceptLoop()
        {
            while (true)
            {
                Socket client;
                try { client = await Listener.AcceptAsync(); }
                catch { break; }
                _ = Task.Run(async () =>
                {
                    try
                    {
                        using var ssl = new SslStream(new NetworkStream(client, ownsSocket: true));
                        await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions
                        {
                            ServerCertificate = Certificate,
                            EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                        });
                        // echo a marker then close
                        var marker = "TLS-OK"u8.ToArray();
                        await ssl.WriteAsync(marker);
                        await ssl.FlushAsync();
                    }
                    catch { }
                });
            }
        }

        public async ValueTask DisposeAsync()
        {
            Listener.Dispose();
            await ValueTask.CompletedTask;
        }
    }

    [Fact]
    public async Task ValidSelfSignedViaCustomRoot_HandshakeSucceeds()
    {
        var (cert, pem) = MakeCert("tunnel.test.example.com");
        await using var server = TlsServer.Start(cert);
        var connector = new TlsConnector(NullLogger<TlsConnector>.Instance);

        var (stream, info) = await connector.ConnectAsync(new TlsOptions
        {
            Host = "127.0.0.1", // dial localhost directly
            Port = server.Port,
            Sni = "tunnel.test.example.com", // SNI + hostname validation target
            TrustedRootPem = pem,
            HandshakeTimeout = TimeSpan.FromSeconds(10)
        }, CancellationToken.None);

        Assert.True(info.HostnameValidated);
        Assert.True(info.ChainValidated);
        Assert.Equal("CN=tunnel.test.example.com", info.Subject);

        using var s = stream;
        var buf = new byte[6];
        var read = 0;
        while (read < 6)
        {
            var n = await s.ReadAsync(buf.AsMemory(read));
            if (n == 0) break;
            read += n;
        }
        Assert.Equal("TLS-OK", System.Text.Encoding.ASCII.GetString(buf));
    }

    [Fact]
    public async Task UntrustedCert_Rejected()
    {
        var (cert, _) = MakeCert("other.test.example.com");
        await using var server = TlsServer.Start(cert);
        var connector = new TlsConnector(NullLogger<TlsConnector>.Instance);

        var ex = await Assert.ThrowsAsync<HAZEM.Tunnel.Core.TunnelException>(() =>
            connector.ConnectAsync(new TlsOptions
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Sni = "other.test.example.com",
                ValidateCertificates = true,
                HandshakeTimeout = TimeSpan.FromSeconds(10)
            }, CancellationToken.None));

        Assert.Equal(HAZEM.Tunnel.Core.Models.TunnelErrorCategory.Certificate, ex.Category);
    }

    [Fact]
    public async Task HostnameMismatch_Rejected()
    {
        // Cert for name A; client validates against name B; custom root trusts the cert,
        // so ONLY the hostname check can (and must) fail.
        var (certA, pemA) = MakeCert("aaa.test.example.com");
        await using var server = TlsServer.Start(certA);
        var connector = new TlsConnector(NullLogger<TlsConnector>.Instance);

        var ex = await Assert.ThrowsAsync<HAZEM.Tunnel.Core.TunnelException>(() =>
            connector.ConnectAsync(new TlsOptions
            {
                Host = "127.0.0.1",
                Port = server.Port,
                Sni = "bbb.test.example.com",
                TrustedRootPem = pemA,
                HandshakeTimeout = TimeSpan.FromSeconds(10)
            }, CancellationToken.None));

        Assert.Equal(HAZEM.Tunnel.Core.Models.TunnelErrorCategory.Certificate, ex.Category);
    }

    [Fact]
    public async Task DnsFailure_ReportedAsDns()
    {
        var connector = new TlsConnector(NullLogger<TlsConnector>.Instance);
        var ex = await Assert.ThrowsAsync<HAZEM.Tunnel.Core.TunnelException>(() =>
            connector.ConnectAsync(new TlsOptions
            {
                Host = "definitely-does-not-exist.invalid.example",
                Port = 443,
                HandshakeTimeout = TimeSpan.FromSeconds(5)
            }, CancellationToken.None));
        Assert.Equal(HAZEM.Tunnel.Core.Models.TunnelErrorCategory.Dns, ex.Category);
    }

    [Fact]
    public async Task ConnectionRefused_ReportedAsNetworkUnreachable()
    {
        var connector = new TlsConnector(NullLogger<TlsConnector>.Instance);
        var ex = await Assert.ThrowsAsync<HAZEM.Tunnel.Core.TunnelException>(() =>
            connector.ConnectAsync(new TlsOptions
            {
                Host = "127.0.0.1",
                Port = 1, // nothing listens here
                HandshakeTimeout = TimeSpan.FromSeconds(5)
            }, CancellationToken.None));
        Assert.Equal(HAZEM.Tunnel.Core.Models.TunnelErrorCategory.NetworkUnreachable, ex.Category);
    }
}

public class CertificateHostnameValidatorTests
{
    [Theory]
    [InlineData("example.com", "example.com", true)]
    [InlineData("EXAMPLE.com", "example.COM", true)]
    [InlineData("*.example.com", "www.example.com", true)]
    [InlineData("*.example.com", "a.b.example.com", false)]   // wildcard covers one label
    [InlineData("*.example.com", "example.com", false)]
    [InlineData("*.com", "anything.com", false)]              // bare TLD wildcard rejected
    [InlineData("example.com", "other.com", false)]
    [InlineData("www-*.example.com", "www-42.example.com", true)]
    public void DnsPatterns_MatchAsSpecified(string pattern, string host, bool expected)
    {
        Assert.Equal(expected, CertificateHostnameValidator.MatchesDns(pattern, host));
    }

    [Fact]
    public void ExtractCn_Works()
    {
        Assert.Equal("example.com", CertificateHostnameValidator.ExtractCn("C=EG, CN=example.com, O=Test"));
    }

    [Fact]
    public void PemLoader_ParsesCertificate()
    {
        using var rsa = System.Security.Cryptography.RSA.Create(2048);
        var req = new System.Security.Cryptography.X509Certificates.CertificateRequest(
            "CN=pemload.test", rsa, System.Security.Cryptography.HashAlgorithmName.SHA256,
            System.Security.Cryptography.RSASignaturePadding.Pkcs1);
        using var cert = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(5));
        var pem = "-----BEGIN CERTIFICATE-----\n" +
                  Convert.ToBase64String(cert.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks) +
                  "\n-----END CERTIFICATE-----\n";
        var loaded = PemCertLoader.LoadAll(pem);
        Assert.Single(loaded);
        Assert.Contains("pemload.test", loaded[0].Subject);
    }
}
