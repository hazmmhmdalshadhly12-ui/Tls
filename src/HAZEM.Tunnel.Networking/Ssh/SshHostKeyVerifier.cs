using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Renci.SshNet.Common;

namespace HAZEM.Tunnel.Networking.Ssh;

/// <summary>Formats and verifies SSH host keys using SHA-256 OpenSSH-style fingerprints.</summary>
public static class HostKeyFormatter
{
    public static string Fingerprint(byte[] hostKey)
    {
        using var sha = SHA256.Create();
        var hash = sha.ComputeHash(hostKey);
        return "SHA256:" + Convert.ToBase64String(hash).TrimEnd('=');
    }
}

/// <summary>Verification policy decision for a presented host key.</summary>
public enum HostKeyDecision
{
    Accepted,
    FirstUseAccepted,
    RejectedChanged,
    RejectedUnknown,
    RejectedNotPinned
}

/// <summary>
/// Verifies SSH host keys against the known-hosts store:
/// TOFU (default), Strict, and Pinned. Never blindly accepts.
/// </summary>
public sealed class SshHostKeyVerifier
{
    private readonly HAZEM.Tunnel.Core.Interfaces.IKnownHostsStore? _store;
    private readonly ConcurrentDictionary<string, string> _sessionKeys = new();

    public SshHostKeyVerifier(HAZEM.Tunnel.Core.Interfaces.IKnownHostsStore? store) => _store = store;

    /// <summary>Verifies a presented host key for host:port under the given policy.</summary>
    public HostKeyDecision Verify(string host, int port, byte[] hostKey,
        HAZEM.Tunnel.Core.Models.SshHostKeyPolicy policy, string? pinnedFingerprint)
    {
        ArgumentNullException.ThrowIfNull(hostKey);
        var fp = HostKeyFormatter.Fingerprint(hostKey);
        var hostKeyToken = $"{host}:{port}";
        _sessionKeys[hostKeyToken] = fp;

        if (policy == HAZEM.Tunnel.Core.Models.SshHostKeyPolicy.Pinned)
            return string.Equals(fp, pinnedFingerprint, StringComparison.OrdinalIgnoreCase)
                ? HostKeyDecision.Accepted
                : HostKeyDecision.RejectedNotPinned;

        if (_store is null)
            return policy == HAZEM.Tunnel.Core.Models.SshHostKeyPolicy.Strict
                ? HostKeyDecision.RejectedUnknown
                : HostKeyDecision.FirstUseAccepted;

        var existing = _store.Find(host, port);
        if (existing is null)
        {
            if (policy == HAZEM.Tunnel.Core.Models.SshHostKeyPolicy.Strict)
                return HostKeyDecision.RejectedUnknown;
            _store.AddOrUpdate(new HAZEM.Tunnel.Core.Models.KnownHostEntry
            {
                Host = host,
                Port = port,
                Fingerprint = fp,
                Algorithm = "ssh",
                FirstSeen = DateTimeOffset.UtcNow,
                LastSeen = DateTimeOffset.UtcNow
            });
            return HostKeyDecision.FirstUseAccepted;
        }

        if (string.Equals(existing.Fingerprint, fp, StringComparison.OrdinalIgnoreCase))
        {
            existing.LastSeen = DateTimeOffset.UtcNow;
            _store.AddOrUpdate(existing);
            return HostKeyDecision.Accepted;
        }
        return HostKeyDecision.RejectedChanged; // MITM or server changed keys — always fail closed.
    }

    public string? GetSessionFingerprint(string host, int port)
        => _sessionKeys.TryGetValue($"{host}:{port}", out var fp) ? fp : null;
}
