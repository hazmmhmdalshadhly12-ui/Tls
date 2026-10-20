namespace HAZEM.Tunnel.Core.Models;

/// <summary>Overall connection state machine exposed to the UI.</summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Failed
}

/// <summary>Ordered stages the tunnel engine walks through before "Connected" is allowed.</summary>
public enum ConnectionStage
{
    Idle,
    ResolvingDns,
    EstablishingTcp,
    EstablishingTls,
    AuthenticatingSsh,
    OpeningChannel,
    StartingProxy,
    HealthCheck,
    Connected,
    Failed
}

/// <summary>Stable machine-readable error categories used by UI messages and logs.</summary>
public enum TunnelErrorCategory
{
    None,
    Configuration,
    Dns,
    NetworkUnreachable,
    ConnectionRefused,
    TlsHandshake,
    Certificate,
    SshAuthentication,
    SshProtocol,
    HostKeyRejected,
    WebSocketHandshake,
    ProxyBind,
    ProxyAuth,
    HealthCheckFailed,
    Cancelled,
    Timeout,
    Unknown
}

/// <summary>Supported tunnel transports.</summary>
public enum TransportKind
{
    /// <summary>Direct TLS connection to the destination (diagnostics / TLS-capable services only).</summary>
    TlsDirect = 0,

    /// <summary>Plain SSH transport (port 22 by default) with local port forwarding.</summary>
    Ssh = 1,

    /// <summary>TLS first (with SNI), then SSH through the TLS bridge.</summary>
    TlsSsh = 2,

    /// <summary>WebSocket (ws:// or wss://) endpoint used as transport.</summary>
    WebSocket = 3
}

/// <summary>SSH authentication modes supported by the app.</summary>
public enum SshAuthMode
{
    Password = 0,
    PrivateKey = 1
}

/// <summary>Host-key verification policies (never "accept anything").</summary>
public enum SshHostKeyPolicy
{
    /// <summary>First use: store fingerprint; later uses must match (TOFU).</summary>
    TrustOnFirstUse = 0,

    /// <summary>Key must already exist in the known-hosts store.</summary>
    Strict = 1,

    /// <summary>Key must match the pinned fingerprint stored in the profile.</summary>
    Pinned = 2
}

/// <summary>Which tunnel transport a profile uses.</summary>
public enum ProxyAuthMode
{
    None = 0,
    UsernamePassword = 1
}
