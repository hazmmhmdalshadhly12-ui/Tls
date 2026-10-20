using System.IO;
using System.Security.Cryptography;
using System.Text;
using HAZEM.Tunnel.Core.Interfaces;

namespace HAZEM.Tunnel.Infrastructure.Credentials;

/// <summary>
/// Windows DPAPI-backed credential store (CurrentUser scope). Values are encrypted at rest
/// and never written to disk in plaintext; the store itself never logs secret content.
/// On non-Windows (tests/dev) use InMemoryCredentialStore.
/// </summary>
[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class DpapiCredentialStore : ICredentialStore
{
    private readonly string _blobDirectory;

    public DpapiCredentialStore(string? directoryOverride = null)
    {
        _blobDirectory = directoryOverride ?? System.IO.Path.Combine(AppPaths.Data, "credentials");
        Directory.CreateDirectory(_blobDirectory);
    }

    private static byte[] Protect(string plaintext) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(plaintext), optionalEntropy: Entropy, DataProtectionScope.CurrentUser);

    private static string Unprotect(byte[] blob) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(blob, Entropy, DataProtectionScope.CurrentUser));

    // App-specific entropy: same key every time for this app install.
    private static readonly byte[] Entropy = "HAZEM.Tunnel::credential-entropy::v1"u8.ToArray();

    private string PathFor(string key) => System.IO.Path.Combine(_blobDirectory, HashKey(key) + ".bin");

    private static string HashKey(string key)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    public void SetSecret(string key, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        var blob = Protect(plaintext);
        AtomicJson.WriteText(PathFor(key), Convert.ToBase64String(blob));
    }

    public string? GetSecret(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return null;
        try
        {
            var base64 = File.ReadAllText(path);
            return Unprotect(Convert.FromBase64String(base64));
        }
        catch (CryptographicException)
        {
            return null; // blob belongs to another user/machine — treat as missing
        }
        catch (FormatException)
        {
            return null;
        }
    }

    public bool RemoveSecret(string key)
    {
        var path = PathFor(key);
        if (!File.Exists(path)) return false;
        File.Delete(path);
        return true;
    }

    public void DeleteAllForPrefix(string prefix)
    {
        // Key material is hashed; prefix deletion scans the in-memory index file.
        var index = LoadIndex();
        foreach (var k in index.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
        {
            RemoveSecret(k);
            index.Remove(k);
        }
        SaveIndex(index);
    }

    private string IndexPath => System.IO.Path.Combine(_blobDirectory, "index.json");
    private HashSet<string> LoadIndex()
    {
        var raw = AtomicJson.Load<IndexModel>(IndexPath);
        return raw?.Keys is { } keys ? new HashSet<string>(keys) : new HashSet<string>();
    }
    private void SaveIndex(HashSet<string> keys) => AtomicJson.Write(IndexPath, new IndexModel { Keys = keys.ToList() });

    private sealed class IndexModel { public List<string> Keys { get; set; } = new(); }
}

/// <summary>Non-persistent store for tests and non-Windows dev environments.</summary>
public sealed class InMemoryCredentialStore : ICredentialStore
{
    private readonly Dictionary<string, string> _secrets = new();

    public void SetSecret(string key, string plaintext)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        lock (_secrets) _secrets[key] = plaintext;
    }

    public string? GetSecret(string key)
    {
        lock (_secrets) return _secrets.TryGetValue(key, out var v) ? v : null;
    }

    public bool RemoveSecret(string key)
    {
        lock (_secrets) return _secrets.Remove(key);
    }

    public void DeleteAllForPrefix(string prefix)
    {
        lock (_secrets)
        {
            foreach (var k in _secrets.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
                _secrets.Remove(k);
        }
    }
}

/// <summary>Picks the right store for the current OS (DPAPI is Windows-only).</summary>
public static class CredentialStoreFactory
{
    public static ICredentialStore Create(string? directoryOverride = null) =>
        OperatingSystem.IsWindows()
            ? new DpapiCredentialStore(directoryOverride)
            : new InMemoryCredentialStore();
}
