using System.IO;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Infrastructure.Configuration;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Infrastructure.Configuration;

/// <summary>
/// File-per-profile store with atomic writes. Corrupt files are quarantined (never crash startup).
/// </summary>
public sealed class JsonProfileStore : IProfileStore
{
    private readonly ILogger<JsonProfileStore> _logger;

    public JsonProfileStore(ILogger<JsonProfileStore> logger, string? directoryOverride = null)
    {
        _logger = logger;
        DirectoryPath = directoryOverride ?? AppPaths.Profiles;
        Directory.CreateDirectory(DirectoryPath);
    }

    public string DirectoryPath { get; }

    private string PathFor(string id) => System.IO.Path.Combine(DirectoryPath, $"profile-{Sanitize(id)}.json");

    private static string Sanitize(string id)
    {
        var invalid = System.IO.Path.GetInvalidFileNameChars();
        return new string(id.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }

    public IReadOnlyList<TunnelProfile> LoadAll()
    {
        var list = new List<TunnelProfile>();
        if (!Directory.Exists(DirectoryPath)) return list;
        foreach (var file in Directory.EnumerateFiles(DirectoryPath, "profile-*.json").OrderBy(f => f))
        {
            try
            {
                var json = File.ReadAllText(file);
                var profile = ProfileJson.Parse(json, System.IO.Path.GetFileName(file));
                list.Add(profile);
            }
            catch (TunnelException te)
            {
                // Quarantine unreadable profile instead of failing startup; surface the reason in logs.
                _logger.LogWarning("Skipping unreadable profile file {File}: {Message}", file, te.Message);
                try
                {
                    var quarantine = file + ".corrupt";
                    if (!File.Exists(quarantine)) File.Move(file, quarantine);
                }
                catch (IOException) { /* leave in place */ }
            }
        }
        return list;
    }

    public void Save(TunnelProfile profile)
    {
        ArgumentNullException.ThrowIfNull(profile);
        profile.UpdatedAt = DateTimeOffset.UtcNow;
        AtomicJson.WriteText(PathFor(profile.Id), ProfileJson.Serialize(profile));
        _logger.LogDebug("Saved profile '{Name}' ({Id}).", profile.Name, profile.Id);
    }

    public void Delete(string id)
    {
        var path = PathFor(id);
        if (File.Exists(path)) File.Delete(path);
    }
}
