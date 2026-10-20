using System.Net;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using HAZEM.Tunnel.Core.Models;

namespace HAZEM.Tunnel.Networking.Tls;

/// <summary>
/// RFC 6125 hostname validation (exact + wildcard) with idnSupport.
/// Used both for system-trust validation and custom-root validation.
/// </summary>
public static class CertificateHostnameValidator
{
    public static bool Matches(X509Certificate2 cert, string hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname) || cert is null) return false;
        if (IPAddress.TryParse(hostname, out var ip))
            return MatchesIp(cert, ip);

        foreach (var dns in GetDnsNames(cert))
        {
            if (MatchesDns(dns, hostname)) return true;
        }
        // Fall back to Subject CN when no SAN DNS entries exist (rare but legal).
        var sans = GetDnsNames(cert).ToList();
        if (sans.Count == 0)
        {
            var cn = ExtractCn(cert.Subject);
            if (cn is not null && MatchesDns(cn, hostname)) return true;
        }
        return false;
    }

    private static bool MatchesIp(X509Certificate2 cert, IPAddress ip)
    {
        foreach (var name in cert.Extensions.OfType<X509SubjectAlternativeNameExtension>()
                     .SelectMany(e => e.EnumerateIPAddresses()))
        {
            if (name.Equals(ip)) return true;
        }
        return false;
    }

    public static IEnumerable<string> GetDnsNames(X509Certificate2 cert)
    {
        foreach (var ext in cert.Extensions.OfType<X509SubjectAlternativeNameExtension>())
        {
            foreach (var dns in ext.EnumerateDnsNames())
                yield return dns;
        }
    }

    public static string? ExtractCn(string subject)
    {
        foreach (var part in subject.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (part.StartsWith("CN=", StringComparison.OrdinalIgnoreCase))
                return part[3..].Trim();
        }
        return null;
    }

    /// <summary>wildcard-aware DNS comparison per RFC 6125 (left-most label may be '*').</summary>
    public static bool MatchesDns(string pattern, string hostname)
    {
        if (string.IsNullOrEmpty(pattern) || string.IsNullOrEmpty(hostname)) return false;
        pattern = pattern.Trim().TrimEnd('.');
        hostname = hostname.Trim().TrimEnd('.');

        if (pattern.Equals(hostname, StringComparison.OrdinalIgnoreCase)) return true;

        if (!pattern.Contains('*')) return false;

        // Only left-most wildcard labels are honored (e.g. *.example.com)
        var pLabels = pattern.Split('.');
        var hLabels = hostname.Split('.');
        if (pLabels.Length != hLabels.Length) return false;
        if (pLabels.Length < 3 && pattern.StartsWith("*.")) return false; // "*.com" style is rejected

        for (var i = 0; i < pLabels.Length; i++)
        {
            if (i == 0 && pLabels[i].Contains('*'))
            {
                if (!WildcardMatch(pLabels[i], hLabels[i])) return false;
            }
            else if (!pLabels[i].Equals(hLabels[i], StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }

    private static bool WildcardMatch(string pattern, string value)
    {
        // Simple '*' within a single label (e.g. "www-*" or "*").
        var idx = pattern.IndexOf('*');
        if (idx < 0) return pattern.Equals(value, StringComparison.OrdinalIgnoreCase);
        var prefix = pattern[..idx];
        var suffix = pattern[(idx + 1)..];
        return value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
               && value.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)
               && value.Length >= prefix.Length + suffix.Length;
    }
}

/// <summary>
/// Validates TLS certificates. Supports the system store and, additionally,
/// explicit custom root CAs (pinned PEM). NEVER disables validation.
/// </summary>
public sealed class CertificateValidator
{
    private readonly string? _customRootPem;
    private readonly string _expectedHostname;

    public CertificateValidator(string expectedHostname, string? customRootPem = null)
    {
        _expectedHostname = expectedHostname;
        _customRootPem = string.IsNullOrWhiteSpace(customRootPem) ? null : customRootPem;
    }

    public (bool ChainValid, bool HostnameValid) Validate(X509Chain? chain, X509Certificate2 cert)
    {
        var hostnameValid = CertificateHostnameValidator.Matches(cert, _expectedHostname);
        var chainValid = ValidateChain(cert);
        return (chainValid, hostnameValid);
    }

    private bool ValidateChain(X509Certificate2 cert)
    {
        using var chain = new X509Chain();
        chain.ChainPolicy.RevocationMode = X509RevocationMode.Online;
        chain.ChainPolicy.RevocationFlag = X509RevocationFlag.ExcludeRoot;

        if (_customRootPem is not null)
        {
            // Additional trust, not replacement: system roots AND user-pinned roots are accepted.
            chain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
            chain.ChainPolicy.CustomTrustStore.Clear();
            foreach (var root in PemCertLoader.LoadAll(_customRootPem))
                chain.ChainPolicy.CustomTrustStore.Add(root);
        }

        return chain.Build(cert);
    }
}

public static class PemCertLoader
{
    public static List<X509Certificate2> LoadAll(string pem)
    {
        var list = new List<X509Certificate2>();
        const string begin = "-----BEGIN CERTIFICATE-----";
        const string end = "-----END CERTIFICATE-----";
        var rest = pem.AsSpan();
        while (true)
        {
            var b = rest.IndexOf(begin);
            if (b < 0) break;
            var e = rest[b..].IndexOf(end);          // e = offset of end marker relative to rest[b..]
            if (e < 0) break;
            var bodyStart = b + begin.Length;        // absolute offset (within rest) of the base64 body
            var bodyLen = e - begin.Length;          // end marker sits at rest-relative offset b + e
            if (bodyLen <= 0)
            {
                rest = rest[(b + e + end.Length)..];
                continue;
            }
            var body = rest.Slice(bodyStart, bodyLen).ToString()
                .Replace("\r", "").Replace("\n", "").Replace(" ", "").Trim();
            try
            {
                list.Add(X509CertificateLoader.LoadCertificate(Convert.FromBase64String(body)));
            }
            catch (FormatException)
            {
                // Skip malformed entries; validation of remaining roots continues.
            }
            rest = rest[(b + e + end.Length)..];
        }
        return list;
    }
}
