using HAZEM.Tunnel.Core.Models;

namespace HAZEM.Tunnel.Core.Interfaces;

/// <summary>Stores and retrieves tunnel profiles atomically.</summary>
public interface IProfileStore
{
    IReadOnlyList<TunnelProfile> LoadAll();
    void Save(TunnelProfile profile);
    void Delete(string id);
    string DirectoryPath { get; }
}

/// <summary>OS-backed secret storage (DPAPI on Windows). Only opaque blobs are persisted.</summary>
public interface ICredentialStore
{
    void SetSecret(string key, string plaintext);
    string? GetSecret(string key);
    bool RemoveSecret(string key);
    void DeleteAllForPrefix(string prefix);
}

/// <summary>Persisted SSH known-hosts (TOFU/Strict support).</summary>
public interface IKnownHostsStore
{
    KnownHostEntry? Find(string host, int port);
    void AddOrUpdate(KnownHostEntry entry);
    bool Remove(string host, int port);
    IReadOnlyList<KnownHostEntry> All();
}

/// <summary>Persisted application settings.</summary>
public interface IAppSettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
