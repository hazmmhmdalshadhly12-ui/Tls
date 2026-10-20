using HAZEM.Tunnel.Core;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Validation;
using HAZEM.Tunnel.Infrastructure.Configuration;
using Xunit;

namespace HAZEM.Tunnel.Configuration.Tests;

public class ProfileJsonTests
{
    private static TunnelProfile Sample() => new()
    {
        Name = "sample",
        TransportKind = TransportKind.Ssh,
        Ssh = new SshOptions
        {
            Host = "ssh.example.com",
            Port = 2222,
            Username = "ali",
            AuthMode = SshAuthMode.Password,
            PasswordRef = new SecretRef("profile:abc:ssh-password"),
            RemoteForwardHost = "10.0.0.5",
            RemoteForwardPort = 80,
            HostKeyPolicy = SshHostKeyPolicy.TrustOnFirstUse
        },
        LocalProxy = new LocalProxyOptions { BindAddress = "127.0.0.1", Port = 1080 }
    };

    [Fact]
    public void SerializeThenParse_RoundTrips()
    {
        var p = Sample();
        var json = ProfileJson.Serialize(p);
        var parsed = ProfileJson.Parse(json);
        Assert.Equal(p.Name, parsed.Name);
        Assert.Equal(p.TransportKind, parsed.TransportKind);
        Assert.Equal(p.Ssh.Host, parsed.Ssh.Host);
        Assert.Equal(p.Ssh.Port, parsed.Ssh.Port);
        Assert.Equal(p.Ssh.Username, parsed.Ssh.Username);
        Assert.Equal(p.Ssh.PasswordRef!.Key, parsed.Ssh.PasswordRef!.Key);
        Assert.Equal(p.Ssh.RemoteForwardHost, parsed.Ssh.RemoteForwardHost);
        Assert.Equal(p.Ssh.RemoteForwardPort, parsed.Ssh.RemoteForwardPort);
        Assert.Equal(p.LocalProxy.Port, parsed.LocalProxy.Port);
        Assert.Equal(p.LocalProxy.BindAddress, parsed.LocalProxy.BindAddress);
    }

    [Fact]
    public void SerializedFile_ContainsNoPlaintextSecretFields()
    {
        var json = ProfileJson.Serialize(Sample());
        // The reference KEY is fine (it is an opaque pointer), but there must be
        // no "password":"<value>" style raw secret anywhere.
        Assert.DoesNotContain("\"password\"", json.Replace("\"passwordRef\"", "", StringComparison.Ordinal));
        Assert.Contains("passwordRef", json);
    }

    [Fact]
    public void UnknownField_Rejected()
    {
        var json = ProfileJson.Serialize(Sample());
        var doctored = json.Replace("\"transportKind\"", "\"totallyUnknownField\": 1, \"transportKind\"");
        var ex = Assert.Throws<TunnelException>(() => ProfileJson.Parse(doctored));
        Assert.Contains("unknown field", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void UnknownNestedField_Rejected()
    {
        var json = ProfileJson.Serialize(Sample())
            .Replace("\"ssh\": {", "\"ssh\": { \"authRetries\": 99,");
        Assert.Throws<TunnelException>(() => ProfileJson.Parse(json));
    }

    [Fact]
    public void RawSecretValue_Rejected()
    {
        const string json = """{"schemaVersion":1,"name":"x","transportKind":"Ssh","ssh":{"host":"h","port":22,"username":"u","authMode":"Password","passwordRef":"RAW-PLAINTEXT","remoteForwardHost":"d","remoteForwardPort":80},"localProxy":{"bindAddress":"127.0.0.1","port":1080}}""";
        var ex = Assert.Throws<TunnelException>(() => ProfileJson.Parse(json));
        Assert.Contains("credential-store reference", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void InvalidJson_ReportsNicely()
    {
        var ex = Assert.Throws<TunnelException>(() => ProfileJson.Parse("{ not json ]", "file.json"));
        Assert.Contains("file.json", ex.Message);
    }

    [Fact]
    public void NewerSchemaVersion_Rejected()
    {
        var json = ProfileJson.Serialize(Sample()).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
        var ex = Assert.Throws<TunnelException>(() => ProfileJson.Parse(json));
        Assert.Contains("newer", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void V0Document_Migrates()
    {
        const string v0 = """
        {
          "schemaVersion": 0,
          "id": "old-id",
          "name": "legacy",
          "type": "sshtunnel",
          "ssh": { "host": "old.example.com", "port": 22, "username": "u",
                   "remoteForwardHost": "1.2.3.4", "remoteForwardPort": 443 },
          "localProxy": { "bindAddress": "127.0.0.1", "port": 1081 }
        }
        """;
        var migrated = ProfileJson.Parse(v0);
        Assert.Equal(TransportKind.Ssh, migrated.TransportKind);
        Assert.Equal("old.example.com", migrated.Ssh.Host);
        Assert.Equal(1081, migrated.LocalProxy.Port);
        Assert.Equal("1.2.3.4", migrated.Ssh.RemoteForwardHost);
    }

    [Fact]
    public void MissingSchemaVersion_TreatedAsV0()
    {
        const string noVersion = """{"name":"x","type":"ssh"}""";
        var p = ProfileJson.Parse(noVersion);
        Assert.Equal(TransportKind.Ssh, p.TransportKind);
    }

    [Fact]
    public void WebSocketHeaders_ParseStrictly()
    {
        var p = new TunnelProfile
        {
            Name = "ws",
            TransportKind = TransportKind.WebSocket,
            WebSocket = new WebSocketOptions
            {
                Url = "wss://ws.example.org/pipe",
                Headers = { ["X-Custom"] = "value" }
            }
        };
        var json = ProfileJson.Serialize(p);
        var parsed = ProfileJson.Parse(json);
        Assert.Equal("value", parsed.WebSocket.Headers["X-Custom"]);
    }

    [Fact]
    public void NonStringHeader_Rejected()
    {
        var p = Sample();
        p.TransportKind = TransportKind.WebSocket;
        p.WebSocket.Url = "ws://h/p";
        var json = ProfileJson.Serialize(p).Replace("\"headers\": {}", "\"headers\": {\"A\": 1}");
        Assert.Throws<TunnelException>(() => ProfileJson.Parse(json));
    }

    [Fact]
    public void ImporterPreview_CommitsOnlyWhenValid()
    {
        var dir = Path.Combine(Path.GetTempPath(), "hazem-tests-" + Guid.NewGuid().ToString("N"));
        using var _ = new DirectoryCleaner(dir);
        var store = new JsonProfileStore(new NullLoggerBridge(), dir);
        var importer = new ProfileImporter(store, new ProfileValidatorStub(), new NullLoggerBridge());

        var good = ProfileJson.Serialize(Sample());
        var preview = importer.Preview(good, "sample.json");
        Assert.True(preview.Validation.IsValid);
        Assert.Equal(ImportAction.Add, preview.Action);
        importer.Commit(preview);
        Assert.Single(store.LoadAll());

        // Same id again → Replace
        var preview2 = importer.Preview(good, "sample.json");
        Assert.Equal(ImportAction.Replace, preview2.Action);

        // Invalid → preview fails, nothing stored
        var bad = ProfileJson.Serialize(Sample()).Replace("\"transportKind\"", "\"junk\": true, \"transportKind\"");
        Assert.Throws<TunnelException>(() => importer.Preview(bad, "bad.json"));
        Assert.Single(store.LoadAll());
    }

    private sealed class DirectoryCleaner : IDisposable
    {
        private readonly string _dir;
        public DirectoryCleaner(string dir) { _dir = dir; Directory.CreateDirectory(dir); }
        public void Dispose() { try { Directory.Delete(_dir, recursive: true); } catch { } }
    }

    private sealed class NullLoggerBridge : Microsoft.Extensions.Logging.ILogger<JsonProfileStore>, Microsoft.Extensions.Logging.ILogger<ProfileImporter>
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => false;
        public void Log<TState>(Microsoft.Extensions.Logging.LogLevel logLevel, Microsoft.Extensions.Logging.EventId eventId,
            TState state, Exception? exception, Func<TState, Exception?, string> formatter) { }
    }

    private sealed class ProfileValidatorStub : ProfileValidator { }
}
