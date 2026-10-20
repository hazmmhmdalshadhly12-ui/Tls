using System.IO;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;

namespace HAZEM.Tunnel.Infrastructure.Persistence;

/// <summary>Known SSH hosts with atomic writes and corrupt-file tolerance.</summary>
public sealed class KnownHostsFileStore : IKnownHostsStore
{
    private readonly string _path;
    private readonly object _gate = new();

    public KnownHostsFileStore(string? pathOverride = null)
    {
        _path = pathOverride ?? Path.Combine(AppPaths.Data, "known-hosts.json");
    }

    private sealed class FileModel { public int SchemaVersion { get; set; } = 1; public List<KnownHostEntry> Hosts { get; set; } = new(); }

    private List<KnownHostEntry> Load()
    {
        var model = AtomicJson.Load<FileModel>(_path);
        return model?.Hosts ?? new List<KnownHostEntry>();
    }

    private void Save(List<KnownHostEntry> hosts)
    {
        AtomicJson.Write(_path, new FileModel { Hosts = hosts });
    }

    public KnownHostEntry? Find(string host, int port)
    {
        lock (_gate) return Load().FirstOrDefault(h => h.Host == host && h.Port == port);
    }

    public void AddOrUpdate(KnownHostEntry entry)
    {
        lock (_gate)
        {
            var hosts = Load();
            var existing = hosts.FirstOrDefault(h => h.Host == entry.Host && h.Port == entry.Port);
            if (existing is null) hosts.Add(entry);
            else
            {
                existing.Fingerprint = entry.Fingerprint;
                existing.Algorithm = entry.Algorithm;
                existing.LastSeen = entry.LastSeen;
            }
            Save(hosts);
        }
    }

    public bool Remove(string host, int port)
    {
        lock (_gate)
        {
            var hosts = Load();
            var removed = hosts.RemoveAll(h => h.Host == host && h.Port == port) > 0;
            if (removed) Save(hosts);
            return removed;
        }
    }

    public IReadOnlyList<KnownHostEntry> All()
    {
        lock (_gate) return Load().OrderBy(h => h.Host).ThenBy(h => h.Port).ToList();
    }
}

/// <summary>App settings (defaults, retention, security toggles).</summary>
public sealed class AppSettingsStore : IAppSettingsStore
{
    private readonly string _path;

    public AppSettingsStore(string? pathOverride = null)
    {
        _path = pathOverride ?? Path.Combine(AppPaths.Data, "settings.json");
    }

    public AppSettings Load() => AtomicJson.Load<AppSettings>(_path) ?? new AppSettings();

    public void Save(AppSettings settings) => AtomicJson.Write(_path, settings);
}
