using System.IO;
using System.Text.Json;
using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Models;

namespace HAZEM.Tunnel.Infrastructure.Configuration;

/// <summary>
/// Strict versioned JSON schema for profiles (schemaVersion 1).
/// Unknown fields are rejected (typo protection), raw secrets in place of refs are rejected,
/// and v0 documents are migrated. All parsing is exception-mapped to TunnelException.
/// </summary>
public static class ProfileJson
{
    public const int CurrentSchemaVersion = 1;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Disallow,
        AllowTrailingCommas = false
    };

    // ---- allowed field maps per level (strictness = typo protection) ----
    private static readonly string[] RootFields =
    [
        "schemaVersion", "id", "name", "transportKind", "tls", "ssh", "webSocket", "localProxy", "retry",
        "connectTimeoutMs", "autoReconnect", "createdAtUtc", "updatedAtUtc"
    ];
    private static readonly string[] TlsFields = ["host", "port", "sni", "validateCertificates", "trustedRootPem", "handshakeTimeoutMs"];
    private static readonly string[] SshFields =
    [
        "host", "port", "username", "authMode", "passwordRef", "privateKeyRef", "passphraseRef",
        "hostKeyPolicy", "pinnedHostKeyFingerprint", "connectTimeoutMs", "remoteForwardHost", "remoteForwardPort"
    ];
    private static readonly string[] WsFields = ["url", "headers", "subprotocol", "handshakeTimeoutMs"];
    private static readonly string[] ProxyFields =
        ["bindAddress", "port", "allowLan", "authMode", "usernameRef", "passwordRef", "idleTimeoutMs", "maxClients"];
    private static readonly string[] RetryFields = ["maxAttempts", "baseDelayMs", "maxDelayMs", "jitterRatio"];
    private static readonly string[] SecretRefFields = ["key"];

    public static string Serialize(TunnelProfile profile)
    {
        var doc = new Dictionary<string, object?>
        {
            ["schemaVersion"] = CurrentSchemaVersion,
            ["id"] = profile.Id,
            ["name"] = profile.Name,
            ["transportKind"] = profile.TransportKind.ToString(),
            ["tls"] = Dict(new()
            {
                ["host"] = profile.Tls.Host,
                ["port"] = profile.Tls.Port,
                ["sni"] = profile.Tls.Sni,
                ["validateCertificates"] = profile.Tls.ValidateCertificates,
                ["trustedRootPem"] = profile.Tls.TrustedRootPem,
                ["handshakeTimeoutMs"] = (int)profile.Tls.HandshakeTimeout.TotalMilliseconds
            }),
            ["ssh"] = Dict(new()
            {
                ["host"] = profile.Ssh.Host,
                ["port"] = profile.Ssh.Port,
                ["username"] = profile.Ssh.Username,
                ["authMode"] = profile.Ssh.AuthMode.ToString(),
                ["passwordRef"] = ToRefDict(profile.Ssh.PasswordRef),
                ["privateKeyRef"] = ToRefDict(profile.Ssh.PrivateKeyRef),
                ["passphraseRef"] = ToRefDict(profile.Ssh.PassphraseRef),
                ["hostKeyPolicy"] = profile.Ssh.HostKeyPolicy.ToString(),
                ["pinnedHostKeyFingerprint"] = profile.Ssh.PinnedHostKeyFingerprint,
                ["connectTimeoutMs"] = (int)profile.Ssh.ConnectTimeout.TotalMilliseconds,
                ["remoteForwardHost"] = profile.Ssh.RemoteForwardHost,
                ["remoteForwardPort"] = profile.Ssh.RemoteForwardPort
            }),
            ["webSocket"] = Dict(new()
            {
                ["url"] = profile.WebSocket.Url,
                ["headers"] = profile.WebSocket.Headers,
                ["subprotocol"] = profile.WebSocket.Subprotocol,
                ["handshakeTimeoutMs"] = (int)profile.WebSocket.HandshakeTimeout.TotalMilliseconds
            }),
            ["localProxy"] = Dict(new()
            {
                ["bindAddress"] = profile.LocalProxy.BindAddress,
                ["port"] = profile.LocalProxy.Port,
                ["allowLan"] = profile.LocalProxy.AllowLan,
                ["authMode"] = profile.LocalProxy.AuthMode.ToString(),
                ["usernameRef"] = ToRefDict(profile.LocalProxy.UsernameRef),
                ["passwordRef"] = ToRefDict(profile.LocalProxy.PasswordRef),
                ["idleTimeoutMs"] = (int)profile.LocalProxy.IdleTimeout.TotalMilliseconds,
                ["maxClients"] = profile.LocalProxy.MaxClients
            }),
            ["retry"] = Dict(new()
            {
                ["maxAttempts"] = profile.Retry.MaxAttempts,
                ["baseDelayMs"] = profile.Retry.BaseDelayMs,
                ["maxDelayMs"] = profile.Retry.MaxDelayMs,
                ["jitterRatio"] = profile.Retry.JitterRatio
            }),
            ["connectTimeoutMs"] = (int)profile.ConnectTimeout.TotalMilliseconds,
            ["autoReconnect"] = profile.AutoReconnect,
            ["createdAtUtc"] = profile.CreatedAt.ToString("O"),
            ["updatedAtUtc"] = profile.UpdatedAt.ToString("O")
        };
        return JsonSerializer.Serialize(doc, new JsonSerializerOptions { WriteIndented = true });
    }

    public static TunnelProfile Parse(string json, string? sourceName = null)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new TunnelException(TunnelErrorCategory.Configuration,
                $"{Display(sourceName)} is not valid JSON: {ex.Message}", ex);
        }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                throw Bad(sourceName, "the top-level value must be a JSON object.");

            var version = GetInt(root, "schemaVersion") ?? 0;
            if (version == 0) root = MigrateV0(root);
            else if (version > CurrentSchemaVersion)
                throw Bad(sourceName, $"schema version {version} is newer than this app supports (v{CurrentSchemaVersion}). Please update HAZEM Tunnel.");

            RejectUnknownFields(root, RootFields, "profile", sourceName);

            var profile = new TunnelProfile
            {
                Id = GetString(root, "id") ?? Guid.NewGuid().ToString("N"),
                Name = GetString(root, "name") ?? "Imported profile",
                TransportKind = ParseEnum<TransportKind>(GetString(root, "transportKind"), "transportKind", sourceName),
                ConnectTimeout = TimeSpan.FromMilliseconds(GetInt(root, "connectTimeoutMs") ?? 30_000),
                AutoReconnect = GetBool(root, "autoReconnect") ?? true,
            };
            if (DateTimeOffset.TryParse(GetString(root, "createdAtUtc"), out var created)) profile.CreatedAt = created;
            if (DateTimeOffset.TryParse(GetString(root, "updatedAtUtc"), out var updated)) profile.UpdatedAt = updated;

            if (root.TryGetProperty("tls", out var tls))
            {
                RejectUnknownFields(tls, TlsFields, "tls", sourceName);
                profile.Tls.Host = GetString(tls, "host") ?? string.Empty;
                profile.Tls.Port = GetInt(tls, "port") ?? 443;
                profile.Tls.Sni = GetString(tls, "sni") ?? string.Empty;
                profile.Tls.ValidateCertificates = GetBool(tls, "validateCertificates") ?? true;
                profile.Tls.TrustedRootPem = GetString(tls, "trustedRootPem");
                profile.Tls.HandshakeTimeout = TimeSpan.FromMilliseconds(GetInt(tls, "handshakeTimeoutMs") ?? 15_000);
            }
            if (root.TryGetProperty("ssh", out var ssh))
            {
                RejectUnknownFields(ssh, SshFields, "ssh", sourceName);
                profile.Ssh.Host = GetString(ssh, "host") ?? string.Empty;
                profile.Ssh.Port = GetInt(ssh, "port") ?? 22;
                profile.Ssh.Username = GetString(ssh, "username") ?? string.Empty;
                profile.Ssh.AuthMode = ParseEnum<SshAuthMode>(GetString(ssh, "authMode"), "ssh.authMode", sourceName, defaultOnMissing: true);
                profile.Ssh.PasswordRef = ParseSecretRef(ssh, "passwordRef");
                profile.Ssh.PrivateKeyRef = ParseSecretRef(ssh, "privateKeyRef");
                profile.Ssh.PassphraseRef = ParseSecretRef(ssh, "passphraseRef");
                profile.Ssh.HostKeyPolicy = ParseEnum<SshHostKeyPolicy>(GetString(ssh, "hostKeyPolicy"), "ssh.hostKeyPolicy", sourceName, defaultOnMissing: true);
                profile.Ssh.PinnedHostKeyFingerprint = GetString(ssh, "pinnedHostKeyFingerprint");
                profile.Ssh.ConnectTimeout = TimeSpan.FromMilliseconds(GetInt(ssh, "connectTimeoutMs") ?? 20_000);
                profile.Ssh.RemoteForwardHost = GetString(ssh, "remoteForwardHost") ?? string.Empty;
                profile.Ssh.RemoteForwardPort = GetInt(ssh, "remoteForwardPort") ?? 0;
            }
            if (root.TryGetProperty("webSocket", out var ws))
            {
                RejectUnknownFields(ws, WsFields, "webSocket", sourceName);
                profile.WebSocket.Url = GetString(ws, "url") ?? string.Empty;
                profile.WebSocket.Subprotocol = GetString(ws, "subprotocol");
                profile.WebSocket.HandshakeTimeout = TimeSpan.FromMilliseconds(GetInt(ws, "handshakeTimeoutMs") ?? 15_000);
                if (ws.TryGetProperty("headers", out var headers) && headers.ValueKind == JsonValueKind.Object)
                {
                    foreach (var h in headers.EnumerateObject())
                    {
                        if (h.Value.ValueKind != JsonValueKind.String)
                            throw Bad(sourceName, $"webSocket.headers.{h.Name} must be a string.");
                        profile.WebSocket.Headers[h.Name] = h.Value.GetString()!;
                    }
                }
            }
            if (root.TryGetProperty("localProxy", out var proxy))
            {
                RejectUnknownFields(proxy, ProxyFields, "localProxy", sourceName);
                profile.LocalProxy.BindAddress = GetString(proxy, "bindAddress") ?? "127.0.0.1";
                profile.LocalProxy.Port = GetInt(proxy, "port") ?? 1080;
                profile.LocalProxy.AllowLan = GetBool(proxy, "allowLan") ?? false;
                profile.LocalProxy.AuthMode = ParseEnum<ProxyAuthMode>(GetString(proxy, "authMode"), "localProxy.authMode", sourceName, defaultOnMissing: true);
                profile.LocalProxy.UsernameRef = ParseSecretRef(proxy, "usernameRef");
                profile.LocalProxy.PasswordRef = ParseSecretRef(proxy, "passwordRef");
                profile.LocalProxy.IdleTimeout = TimeSpan.FromMilliseconds(GetInt(proxy, "idleTimeoutMs") ?? 600_000);
                profile.LocalProxy.MaxClients = GetInt(proxy, "maxClients") ?? 64;
            }
            if (root.TryGetProperty("retry", out var retry))
            {
                RejectUnknownFields(retry, RetryFields, "retry", sourceName);
                profile.Retry.MaxAttempts = GetInt(retry, "maxAttempts") ?? 5;
                profile.Retry.BaseDelayMs = GetInt(retry, "baseDelayMs") ?? 1000;
                profile.Retry.MaxDelayMs = GetInt(retry, "maxDelayMs") ?? 30_000;
                profile.Retry.JitterRatio = GetDouble(retry, "jitterRatio") ?? 0.2;
            }

            return profile;
        }
    }

    /// <summary>v0 → v1 migration: renames and reshapes the older draft layout.</summary>
    private static JsonElement MigrateV0(JsonElement root)
    {
        var map = new Dictionary<string, object?>();
        foreach (var p in root.EnumerateObject())
        {
            switch (p.Name.ToLowerInvariant())
            {
                case "type": map["transportKind"] = MapV0Transport(p.Value); break;
                case "server": map["ssh"] = p.Value; break;
                case "proxy": map["localProxy"] = p.Value; break;
                default: map[p.Name] = JsonSerializer.Deserialize<JsonElement>(p.Value.GetRawText()); break;
            }
        }
        map["schemaVersion"] = 1;
        return JsonSerializer.SerializeToElement(map);
    }

    private static string MapV0Transport(JsonElement value)
    {
        var v = value.GetString()?.ToLowerInvariant();
        return v switch
        {
            "ssh" or "sshtunnel" => "Ssh",
            "tls" or "tlsdirect" => "TlsDirect",
            "tlsssh" or "tls+ssh" => "TlsSsh",
            "websocket" or "ws" => "WebSocket",
            _ => v ?? "Ssh"
        };
    }

    // ---- helpers ----
    private static Dictionary<string, object?> Dict(Dictionary<string, object?> d) => d;

    /// <summary>Refs are written as { "key": ... } objects — matching the strict parser.</summary>
    private static Dictionary<string, object?>? ToRefDict(SecretRef? reference) =>
        reference is null ? null : new Dictionary<string, object?> { ["key"] = reference.Key };

    private static TunnelException Bad(string? source, string problem) =>
        new(TunnelErrorCategory.Configuration, $"{Display(source)}: {problem}");

    private static string Display(string? source) => string.IsNullOrWhiteSpace(source) ? "Profile" : $"'{source}'";

    private static void RejectUnknownFields(JsonElement obj, string[] allowed, string area, string? source)
    {
        if (obj.ValueKind != JsonValueKind.Object) throw Bad(source, $"{area} must be an object.");
        foreach (var p in obj.EnumerateObject())
        {
            if (!allowed.Contains(p.Name, StringComparer.Ordinal))
                throw Bad(source, $"unknown field '{area}.{p.Name}'. Allowed: {string.Join(", ", allowed)}");
        }
    }

    private static SecretRef? ParseSecretRef(JsonElement obj, string field)
    {
        if (!obj.TryGetProperty(field, out var el)) return null;
        switch (el.ValueKind)
        {
            case JsonValueKind.Null:
                return null;
            case JsonValueKind.String:
                var s = el.GetString();
                if (string.IsNullOrWhiteSpace(s)) return null;
                // Security: raw secrets are never accepted where a reference belongs.
                throw Bad(null, $"'{field}' must be a {{ \"key\": ... }} credential-store reference, not a plaintext value.");
            case JsonValueKind.Object:
                RejectUnknownFields(el, SecretRefFields, field, null);
                var key = GetString(el, "key") ?? throw Bad(null, $"'{field}.key' is required.");
                if (string.IsNullOrWhiteSpace(key)) throw Bad(null, $"'{field}.key' must not be empty.");
                return new SecretRef(key);
            default:
                throw Bad(null, $"'{field}' must be an object with a 'key' property.");
        }
    }

    private static T ParseEnum<T>(string? value, string field, string? source, bool defaultOnMissing = false)
        where T : struct, Enum
    {
        if (value is null)
        {
            if (defaultOnMissing) return default;
            throw Bad(source, $"missing required field '{field}'.");
        }
        if (Enum.TryParse<T>(value, ignoreCase: true, out var parsed)) return parsed;
        throw Bad(source, $"'{field}' value '{value}' is not one of: {string.Join(", ", Enum.GetNames<T>())}.");
    }

    private static string? GetString(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? GetInt(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static double? GetDouble(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool? GetBool(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True ? true
        : el.TryGetProperty(name, out var v2) && v2.ValueKind == JsonValueKind.False ? false : null;
}
