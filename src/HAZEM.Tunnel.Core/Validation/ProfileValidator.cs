using System.Net;
using HAZEM.Tunnel.Core.Models;

namespace HAZEM.Tunnel.Core.Validation;

/// <summary>
/// Transport-specific profile validation with explicit security rules.
/// Validation is strict on purpose: bad configs must fail BEFORE connecting.
/// </summary>
public class ProfileValidator
{
    public virtual ValidationResult Validate(TunnelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var result = ValidationResult.Ok();

        if (string.IsNullOrWhiteSpace(profile.Name))
            result.Add(nameof(profile.Name), "Profile name is required.");

        ValidateLocalProxy(profile, result);

        switch (profile.TransportKind)
        {
            case TransportKind.TlsDirect: ValidateTls(profile.Tls, result, required: true); break;
            case TransportKind.Ssh: ValidateSsh(profile.Ssh, result); break;
            case TransportKind.TlsSsh: ValidateTls(profile.Tls, result, required: true); ValidateSsh(profile.Ssh, result); break;
            case TransportKind.WebSocket: ValidateWebSocket(profile.WebSocket, result); break;
            default: result.Add(nameof(profile.TransportKind), "Unsupported transport kind."); break;
        }

        ValidateRetry(profile.Retry, result);
        return result;
    }

    private static void ValidateLocalProxy(TunnelProfile profile, ValidationResult result)
    {
        var p = profile.LocalProxy;
        if (p.Port is < 1 or > 65535)
            result.Add("LocalProxy.Port", "Local SOCKS5 port must be between 1 and 65535.");

        if (string.IsNullOrWhiteSpace(p.BindAddress))
        {
            result.Add("LocalProxy.BindAddress", "Bind address is required (use 127.0.0.1 unless you explicitly need LAN access).");
        }
        else if (!IPAddress.TryParse(p.BindAddress, out var ip))
        {
            result.Add("LocalProxy.BindAddress", "Bind address must be a literal IP address.");
        }
        else if (!IPAddress.IsLoopback(ip) && !p.AllowLan)
        {
            // Security rule: never silently bind a non-loopback interface.
            result.Add("LocalProxy.BindAddress", "Non-loopback bind requires AllowLan=true (an open proxy on your network is a security risk).");
        }

        if (p.MaxClients is < 1 or > 1024)
            result.Add("LocalProxy.MaxClients", "Max clients must be between 1 and 1024.");

        if (p.AuthMode == ProxyAuthMode.UsernamePassword)
        {
            if (p.UsernameRef is null) result.Add("LocalProxy.UsernameRef", "SOCKS5 username must be stored in the credential store (no plaintext allowed).");
            if (p.PasswordRef is null) result.Add("LocalProxy.PasswordRef", "SOCKS5 password must be stored in the credential store (no plaintext allowed).");
        }

        if (p.IdleTimeout < TimeSpan.FromSeconds(10))
            result.Add("LocalProxy.IdleTimeout", "Idle timeout must be at least 10 seconds.");
    }

    protected virtual void ValidateTls(TlsOptions tls, ValidationResult result, bool required)
    {
        if (required && string.IsNullOrWhiteSpace(tls.Host))
        {
            result.Add("Tls.Host", "TLS host is required.");
            return;
        }
        if (!string.IsNullOrWhiteSpace(tls.Host) && Uri.CheckHostName(tls.Host) == UriHostNameType.Unknown)
            result.Add("Tls.Host", $"'{tls.Host}' is not a valid hostname or IP.");

        if (tls.Port is < 1 or > 65535)
            result.Add("Tls.Port", "TLS port must be between 1 and 65535.");

        // SNI: validated explicitly; may legitimately differ from Host (that's the point of SNI).
        if (!string.IsNullOrWhiteSpace(tls.Sni) && Uri.CheckHostName(tls.Sni) == UriHostNameType.Unknown)
            result.Add("Tls.Sni", $"'{tls.Sni}' is not a valid SNI hostname (must be a DNS name, not a URL or IP literal).");

        if (!tls.ValidateCertificates)
        {
            // The app does not support disabling validation — this rule documents that hard requirement.
            result.Add("Tls.ValidateCertificates", "Certificate validation cannot be disabled in HAZEM Tunnel.");
        }

        if (tls.HandshakeTimeout < TimeSpan.FromSeconds(3))
            result.Add("Tls.HandshakeTimeout", "TLS handshake timeout must be at least 3 seconds.");
    }

    protected virtual void ValidateSsh(SshOptions ssh, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(ssh.Host))
            result.Add("Ssh.Host", "SSH host is required.");
        else if (Uri.CheckHostName(ssh.Host) == UriHostNameType.Unknown)
            result.Add("Ssh.Host", $"'{ssh.Host}' is not a valid hostname or IP.");

        if (ssh.Port is < 1 or > 65535)
            result.Add("Ssh.Port", "SSH port must be between 1 and 65535.");

        if (string.IsNullOrWhiteSpace(ssh.Username))
            result.Add("Ssh.Username", "SSH username is required.");

        switch (ssh.AuthMode)
        {
            case SshAuthMode.Password when ssh.PasswordRef is null:
                result.Add("Ssh.PasswordRef", "Password authentication requires a credential-store reference (no plaintext passwords in profiles).");
                break;
            case SshAuthMode.PrivateKey when ssh.PrivateKeyRef is null:
                result.Add("Ssh.PrivateKeyRef", "Private-key authentication requires a credential-store reference to the PEM key.");
                break;
        }

        if (ssh.HostKeyPolicy == SshHostKeyPolicy.Pinned)
        {
            if (string.IsNullOrWhiteSpace(ssh.PinnedHostKeyFingerprint))
                result.Add("Ssh.PinnedHostKeyFingerprint", "Pinned policy requires a SHA-256 fingerprint ('SHA256:...').");
            else if (!IsValidSha256Fingerprint(ssh.PinnedHostKeyFingerprint))
                result.Add("Ssh.PinnedHostKeyFingerprint", "Fingerprint must look like 'SHA256:<43 base64 chars>'.");
        }

        if (ssh.ConnectTimeout < TimeSpan.FromSeconds(3))
            result.Add("Ssh.ConnectTimeout", "SSH connect timeout must be at least 3 seconds.");

        // Remote forward target is required for SSH-bearing transports: without it there is nothing to reach.
        if (string.IsNullOrWhiteSpace(ssh.RemoteForwardHost))
            result.Add("Ssh.RemoteForwardHost", "Remote forward host (destination behind the SSH server) is required.");
        if (ssh.RemoteForwardPort is < 1 or > 65535)
            result.Add("Ssh.RemoteForwardPort", "Remote forward port must be between 1 and 65535.");
    }

    protected virtual void ValidateWebSocket(WebSocketOptions ws, ValidationResult result)
    {
        if (string.IsNullOrWhiteSpace(ws.Url))
        {
            result.Add("WebSocket.Url", "WebSocket URL is required.");
            return;
        }
        if (!Uri.TryCreate(ws.Url, UriKind.Absolute, out var uri))
        {
            result.Add("WebSocket.Url", $"'{ws.Url}' is not a valid absolute URL.");
            return;
        }
        if (uri.Scheme is not ("ws" or "wss"))
            result.Add("WebSocket.Url", "Scheme must be ws:// or wss://.");
        if (string.IsNullOrEmpty(uri.Host))
            result.Add("WebSocket.Url", "URL must contain a host.");
        if (string.IsNullOrEmpty(uri.PathAndQuery) || uri.PathAndQuery == "/")
            result.Add("WebSocket.Url", "URL must contain a path (e.g. /tunnel).");

        foreach (var key in ws.Headers.Keys)
        {
            if (key.Equals("Host", StringComparison.OrdinalIgnoreCase))
                result.Add("WebSocket.Headers", "Do not set the Host header manually — it is derived from the endpoint URI.");
            if (key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
                result.Add("WebSocket.Headers", $"'{key}' is reserved by the WebSocket handshake.");
        }
    }

    private static void ValidateRetry(RetryOptions retry, ValidationResult result)
    {
        if (retry.MaxAttempts is < 1 or > 100)
            result.Add("Retry.MaxAttempts", "Max attempts must be between 1 and 100.");
        if (retry.BaseDelayMs is < 100 or > 60_000)
            result.Add("Retry.BaseDelayMs", "Base delay must be between 100 and 60000 ms.");
        if (retry.MaxDelayMs < retry.BaseDelayMs)
            result.Add("Retry.MaxDelayMs", "Max delay must be >= base delay.");
        if (retry.JitterRatio is < 0 or > 1)
            result.Add("Retry.JitterRatio", "Jitter ratio must be between 0 and 1.");
    }

    private static bool IsValidSha256Fingerprint(string value)
    {
        if (!value.StartsWith("SHA256:", StringComparison.OrdinalIgnoreCase)) return false;
        var b64 = value["SHA256:".Length..];
        // OpenSSH style: 43 unpadded base64 chars (standard or url-safe alphabet).
        if (b64.Length != 43) return false;
        foreach (var c in b64)
        {
            if (!(char.IsAsciiLetterOrDigit(c) || c is '+' or '/' or '-' or '_')) return false;
        }
        return true;
    }
}
