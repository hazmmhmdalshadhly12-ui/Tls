using System.IO;
using System.Text.Json;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;

namespace HAZEM.Tunnel.Infrastructure;

/// <summary>Canonical on-disk layout for the app (per-user).</summary>
public static class AppPaths
{
    public static string Root { get; private set; } = DefaultRoot();
    public static string Profiles => Path.Combine(Root, "profiles");
    public static string Logs => Path.Combine(Root, "logs");
    public static string Data => Path.Combine(Root, "data");

    private static string DefaultRoot()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(localAppData, "HAZEMTunnel");
    }

    /// <summary>Overrides the root (used by tests). Creates all directories.</summary>
    public static void Initialize(string? rootOverride = null)
    {
        if (!string.IsNullOrWhiteSpace(rootOverride)) Root = rootOverride;
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Profiles);
        Directory.CreateDirectory(Logs);
        Directory.CreateDirectory(Data);
    }
}

/// <summary>Atomic JSON file helpers: temp file + rename, corrupt-file quarantine.</summary>
public static class AtomicJson
{
    private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

    public static void Write(string path, object model)
    {
        WriteText(path, JsonSerializer.Serialize(model, Pretty));
    }

    public static void WriteText(string path, string content)
    {
        var dir = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(dir);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    public static T? Load<T>(string path) where T : class
    {
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path));
        }
        catch (JsonException)
        {
            var quarantine = path + ".corrupt-" + DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmss");
            try { File.Move(path, quarantine); } catch (IOException) { }
            return null;
        }
    }
}
