namespace HAZEM.Tunnel.Core.Models;

/// <summary>Certificate details surfaced to the user after a TLS handshake (never secret material).</summary>
public sealed record CertificateInfo(
    string Subject,
    string Issuer,
    DateTimeOffset NotBefore,
    DateTimeOffset NotAfter,
    string Sha256Thumbprint,
    IReadOnlyList<string> DnsNames,
    bool ChainValidated,
    bool HostnameValidated);

/// <summary>Result of one health check (either through the tunnel or transport-level).</summary>
public sealed record HealthCheckResult(
    bool Success,
    int? HttpStatusCode,
    TimeSpan Latency,
    string Detail,
    bool ThroughTunnel);

/// <summary>Result of testing an SSH server from the Servers page.</summary>
public sealed record SshTestResult(
    bool Success,
    string Message,
    string? HostKeyFingerprint,
    TimeSpan Elapsed);

/// <summary>One immutable line for the UI event log.</summary>
public sealed record UiEvent(DateTimeOffset Timestamp, string Level, string Message);

/// <summary>Known SSH host entry persisted in the known-hosts store.</summary>
public sealed class KnownHostEntry
{
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 22;
    /// <summary>SHA-256 fingerprint in OpenSSH format: "SHA256:base64".</summary>
    public string Fingerprint { get; set; } = string.Empty;
    public string Algorithm { get; set; } = string.Empty;
    public DateTimeOffset FirstSeen { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset LastSeen { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>Application-level settings persisted next to profiles.</summary>
public sealed class AppSettings
{
    public int SchemaVersion { get; set; } = 1;
    /// <summary>Default SOCKS5 port offered when creating profiles.</summary>
    public int DefaultSocksPort { get; set; } = 1080;
    /// <summary>Seconds between periodic health checks while connected (0 disables).</summary>
    public int HealthCheckIntervalSeconds { get; set; } = 30;
    /// <summary>Consecutive health failures before the engine restarts the tunnel.</summary>
    public int HealthFailuresBeforeRestart { get; set; } = 3;
    public int LogRetentionDays { get; set; } = 14;
    public long LogMaxFileSizeBytes { get; set; } = 10 * 1024 * 1024;
    public bool RedactSecretsInLogs { get; set; } = true;
}
