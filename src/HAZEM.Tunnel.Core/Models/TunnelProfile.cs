namespace HAZEM.Tunnel.Core.Models;

/// <summary>
/// A reference to a secret stored in the OS credential store (DPAPI on Windows).
/// Profiles NEVER contain plaintext secrets — only opaque references like "profile:{id}:ssh-password".
/// </summary>
public sealed record SecretRef(string Key)
{
    public override string ToString() => $"ref:{Key}";
}

public sealed class TlsOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 443;
    /// <summary>ServerName used in the TLS ClientHello (SNI) and certificate hostname check.
    /// Empty means "use Host". Kept separate on purpose: HTTP Host and SNI are different concepts.</summary>
    public string Sni { get; set; } = string.Empty;
    public bool ValidateCertificates { get; set; } = true;
    /// <summary>Optional PEM(s) of a custom root CA to additionally trust. Null/empty = system trust only.</summary>
    public string? TrustedRootPem { get; set; }
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

public sealed class SshOptions
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    public string Username { get; set; } = string.Empty;
    public SshAuthMode AuthMode { get; set; } = SshAuthMode.Password;
    /// <summary>Credential-store reference for the password (AuthMode=Password).</summary>
    public SecretRef? PasswordRef { get; set; }
    /// <summary>Credential-store reference for the PEM private key content (AuthMode=PrivateKey).</summary>
    public SecretRef? PrivateKeyRef { get; set; }
    /// <summary>Credential-store reference for the key passphrase (optional).</summary>
    public SecretRef? PassphraseRef { get; set; }
    public SshHostKeyPolicy HostKeyPolicy { get; set; } = SshHostKeyPolicy.TrustOnFirstUse;
    /// <summary>Required when HostKeyPolicy=Pinned: base64 SHA-256 fingerprint ("SHA256:...").</summary>
    public string? PinnedHostKeyFingerprint { get; set; }
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(20);
    /// <summary>The remote destination the SSH server forwards to (what the exit sees). Only relevant for SSH-based transports.</summary>
    public string RemoteForwardHost { get; set; } = string.Empty;
    public int RemoteForwardPort { get; set; }
}

public sealed class WebSocketOptions
{
    /// <summary>Full endpoint: ws://host:port/path or wss://host:port/path. Parsed, never guessed.</summary>
    public string Url { get; set; } = string.Empty;
    /// <summary>Extra handshake headers (name/value). Host header is validated separately.</summary>
    public Dictionary<string, string> Headers { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string? Subprotocol { get; set; }
    public TimeSpan HandshakeTimeout { get; set; } = TimeSpan.FromSeconds(15);
}

public sealed class LocalProxyOptions
{
    /// <summary>Defaults to loopback on purpose. Non-loopback binding requires explicit AllowLan.</summary>
    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 1080;
    public bool AllowLan { get; set; }
    public ProxyAuthMode AuthMode { get; set; } = ProxyAuthMode.None;
    /// <summary>Credential-store reference for the SOCKS5 username (AuthMode=UsernamePassword).</summary>
    public SecretRef? UsernameRef { get; set; }
    /// <summary>Credential-store reference for the SOCKS5 password (AuthMode=UsernamePassword).</summary>
    public SecretRef? PasswordRef { get; set; }
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public int MaxClients { get; set; } = 64;
}

public sealed class RetryOptions
{
    public int MaxAttempts { get; set; } = 5;
    public int BaseDelayMs { get; set; } = 1000;
    public int MaxDelayMs { get; set; } = 30_000;
    /// <summary>0..1 fraction of delay used as jitter.</summary>
    public double JitterRatio { get; set; } = 0.2;
}

/// <summary>
/// A complete, validated tunnel profile. Secret fields are references only;
/// plaintext secrets live in the OS credential store and are resolved at connect time.
/// </summary>
public sealed class TunnelProfile
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New profile";
    public TransportKind TransportKind { get; set; } = TransportKind.Ssh;
    public TlsOptions Tls { get; set; } = new();
    public SshOptions Ssh { get; set; } = new();
    public WebSocketOptions WebSocket { get; set; } = new();
    public LocalProxyOptions LocalProxy { get; set; } = new();
    public RetryOptions Retry { get; set; } = new();
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(30);
    public bool AutoReconnect { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Human readable summary used by the UI list (no secrets — refs are opaque).</summary>
    public string Describe() => TransportKind switch
    {
        TransportKind.TlsDirect => $"TLS {Tls.Host}:{Tls.Port} (SNI {(string.IsNullOrWhiteSpace(Tls.Sni) ? Tls.Host : Tls.Sni)})",
        TransportKind.Ssh => $"SSH {Ssh.Host}:{Ssh.Port} → {Ssh.RemoteForwardHost}:{Ssh.RemoteForwardPort}",
        TransportKind.TlsSsh => $"TLS({(string.IsNullOrWhiteSpace(Tls.Sni) ? Tls.Host : Tls.Sni)})→SSH {Ssh.Host}:{Ssh.Port} → {Ssh.RemoteForwardHost}:{Ssh.RemoteForwardPort}",
        TransportKind.WebSocket => $"WS {WebSocket.Url}",
        _ => Name
    };

    public TunnelProfile Clone() => new()
    {
        Id = Id,
        Name = Name,
        TransportKind = TransportKind,
        Tls = new TlsOptions
        {
            Host = Tls.Host, Port = Tls.Port, Sni = Tls.Sni,
            ValidateCertificates = Tls.ValidateCertificates,
            TrustedRootPem = Tls.TrustedRootPem,
            HandshakeTimeout = Tls.HandshakeTimeout
        },
        Ssh = new SshOptions
        {
            Host = Ssh.Host, Port = Ssh.Port, Username = Ssh.Username, AuthMode = Ssh.AuthMode,
            PasswordRef = Ssh.PasswordRef, PrivateKeyRef = Ssh.PrivateKeyRef, PassphraseRef = Ssh.PassphraseRef,
            HostKeyPolicy = Ssh.HostKeyPolicy, PinnedHostKeyFingerprint = Ssh.PinnedHostKeyFingerprint,
            ConnectTimeout = Ssh.ConnectTimeout,
            RemoteForwardHost = Ssh.RemoteForwardHost, RemoteForwardPort = Ssh.RemoteForwardPort
        },
        WebSocket = new WebSocketOptions
        {
            Url = WebSocket.Url,
            Headers = new Dictionary<string, string>(WebSocket.Headers, StringComparer.OrdinalIgnoreCase),
            Subprotocol = WebSocket.Subprotocol,
            HandshakeTimeout = WebSocket.HandshakeTimeout
        },
        LocalProxy = new LocalProxyOptions
        {
            BindAddress = LocalProxy.BindAddress, Port = LocalProxy.Port, AllowLan = LocalProxy.AllowLan,
            AuthMode = LocalProxy.AuthMode, UsernameRef = LocalProxy.UsernameRef, PasswordRef = LocalProxy.PasswordRef,
            IdleTimeout = LocalProxy.IdleTimeout, MaxClients = LocalProxy.MaxClients
        },
        Retry = new RetryOptions
        {
            MaxAttempts = Retry.MaxAttempts, BaseDelayMs = Retry.BaseDelayMs,
            MaxDelayMs = Retry.MaxDelayMs, JitterRatio = Retry.JitterRatio
        },
        ConnectTimeout = ConnectTimeout,
        AutoReconnect = AutoReconnect,
        CreatedAt = CreatedAt,
        UpdatedAt = UpdatedAt
    };
}
