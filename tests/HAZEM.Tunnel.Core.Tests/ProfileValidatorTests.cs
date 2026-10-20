using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Validation;
using Xunit;

namespace HAZEM.Tunnel.Core.Tests;

public class ProfileValidatorTests
{
    private readonly ProfileValidator _validator = new();

    private static TunnelProfile SshProfile() => new()
    {
        Name = "test",
        TransportKind = TransportKind.Ssh,
        Ssh = new SshOptions
        {
            Host = "ssh.example.com",
            Port = 22,
            Username = "user",
            AuthMode = SshAuthMode.Password,
            PasswordRef = new SecretRef("p:key"),
            RemoteForwardHost = "10.0.0.5",
            RemoteForwardPort = 80
        }
    };

    // ---------- valid baseline ----------
    [Fact]
    public void ValidSshProfile_Passes()
    {
        var r = _validator.Validate(SshProfile());
        Assert.True(r.IsValid, string.Join("; ", r.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void ValidTlsProfile_Passes()
    {
        var p = new TunnelProfile
        {
            Name = "tls",
            TransportKind = TransportKind.TlsDirect,
            Tls = new TlsOptions { Host = "m.facebook.com", Port = 443, Sni = "m.facebook.com" }
        };
        var r = _validator.Validate(p);
        Assert.True(r.IsValid, string.Join("; ", r.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void ValidTlsSshProfile_Passes()
    {
        var p = new TunnelProfile
        {
            Name = "combo",
            TransportKind = TransportKind.TlsSsh,
            Tls = new TlsOptions { Host = "cdn.example.net", Sni = "cdn.example.net" },
            Ssh = SshProfile().Ssh
        };
        var r = _validator.Validate(p);
        Assert.True(r.IsValid, string.Join("; ", r.Issues.Select(i => i.Message)));
    }

    [Fact]
    public void ValidWebSocketProfile_Passes()
    {
        var p = new TunnelProfile
        {
            Name = "ws",
            TransportKind = TransportKind.WebSocket,
            WebSocket = new WebSocketOptions { Url = "wss://ws.example.org/tunnel" }
        };
        var r = _validator.Validate(p);
        Assert.True(r.IsValid, string.Join("; ", r.Issues.Select(i => i.Message)));
    }

    // ---------- SSH rules ----------
    [Fact]
    public void SshWithoutPasswordRef_Fails()
    {
        var p = SshProfile();
        p.Ssh.PasswordRef = null;
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "Ssh.PasswordRef");
    }

    [Fact]
    public void SshKeyAuthWithoutKeyRef_Fails()
    {
        var p = SshProfile();
        p.Ssh.AuthMode = SshAuthMode.PrivateKey;
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "Ssh.PrivateKeyRef");
    }

    [Fact]
    public void SshWithoutRemoteForward_Fails()
    {
        var p = SshProfile();
        p.Ssh.RemoteForwardHost = "";
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "Ssh.RemoteForwardHost");
    }

    [Fact]
    public void PinnedPolicyWithoutFingerprint_Fails()
    {
        var p = SshProfile();
        p.Ssh.HostKeyPolicy = SshHostKeyPolicy.Pinned;
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "Ssh.PinnedHostKeyFingerprint");
    }

    [Theory]
    [InlineData("SHA256:abcdefghijklmnopqrstuvwxyzABCDE012345678901", true)]   // exactly 43 chars
    [InlineData("sha256:abcdefghijklmnopqrstuvwxyzABCDE012345678901", true)]   // case-insensitive prefix
    [InlineData("SHA256:abcdefghijklmnopqrstuvwxyzABCDE0123456789-_", true)]   // url-safe alphabet ok
    [InlineData("md5:abcd", false)]
    [InlineData("SHA256:short", false)]
    [InlineData("SHA256:abcdefghijklmnopqrstuvwxyzABCDE01234567890123", false)] // wrong length (45)
    [InlineData("SHA256:abcdefghijklmnopqrstuvwxyzABCDE012345678901$", false)]  // invalid char
    public void PinnedFingerprintFormat_Validated(string fp, bool ok)
    {
        var p = SshProfile();
        p.Ssh.HostKeyPolicy = SshHostKeyPolicy.Pinned;
        p.Ssh.PinnedHostKeyFingerprint = fp;
        var r = _validator.Validate(p);
        Assert.Equal(ok, r.IsValid);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(70000)]
    [InlineData(-1)]
    public void SshPortRange_Enforced(int port)
    {
        var p = SshProfile();
        p.Ssh.Port = port;
        Assert.False(_validator.Validate(p).IsValid);
    }

    // ---------- TLS rules ----------
    [Fact]
    public void TlsWithoutHost_Fails()
    {
        var p = new TunnelProfile { Name = "x", TransportKind = TransportKind.TlsDirect };
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "Tls.Host");
    }

    [Fact]
    public void TlsWithInvalidSni_Fails()
    {
        var p = new TunnelProfile
        {
            Name = "x",
            TransportKind = TransportKind.TlsDirect,
            Tls = new TlsOptions { Host = "ok.example.com", Sni = "https://wrong.example.com" }
        };
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "Tls.Sni");
    }

    [Fact]
    public void DisablingCertificateValidation_IsRejected()
    {
        var p = new TunnelProfile
        {
            Name = "x",
            TransportKind = TransportKind.TlsDirect,
            Tls = new TlsOptions { Host = "ok.example.com", ValidateCertificates = false }
        };
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "Tls.ValidateCertificates");
    }

    // ---------- WebSocket rules ----------
    [Fact]
    public void WsUrlWithoutPath_Fails()
    {
        var p = new TunnelProfile
        {
            Name = "x",
            TransportKind = TransportKind.WebSocket,
            WebSocket = new WebSocketOptions { Url = "wss://ws.example.org" }
        };
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "WebSocket.Url");
    }

    [Fact]
    public void WsHttpScheme_Fails()
    {
        var p = new TunnelProfile
        {
            Name = "x",
            TransportKind = TransportKind.WebSocket,
            WebSocket = new WebSocketOptions { Url = "http://ws.example.org/tunnel" }
        };
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "WebSocket.Url");
    }

    [Fact]
    public void WsReservedHeader_Fails()
    {
        var p = new TunnelProfile
        {
            Name = "x",
            TransportKind = TransportKind.WebSocket,
            WebSocket = new WebSocketOptions
            {
                Url = "ws://ws.example.org/tunnel",
                Headers = { ["Sec-WebSocket-Key"] = "zzz" }
            }
        };
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "WebSocket.Headers");
    }

    // ---------- Proxy security rules ----------
    [Fact]
    public void NonLoopbackBindWithoutAllowLan_Fails()
    {
        var p = SshProfile();
        p.LocalProxy.BindAddress = "192.168.1.10";
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "LocalProxy.BindAddress");
    }

    [Fact]
    public void NonLoopbackBindWithAllowLan_Passes()
    {
        var p = SshProfile();
        p.LocalProxy.BindAddress = "192.168.1.10";
        p.LocalProxy.AllowLan = true;
        Assert.True(_validator.Validate(p).IsValid);
    }

    [Fact]
    public void ProxyAuthWithoutRefs_Fails()
    {
        var p = SshProfile();
        p.LocalProxy.AuthMode = ProxyAuthMode.UsernamePassword;
        var r = _validator.Validate(p);
        Assert.Contains(r.Issues, i => i.Field == "LocalProxy.UsernameRef");
        Assert.Contains(r.Issues, i => i.Field == "LocalProxy.PasswordRef");
    }

    // ---------- Retry rules ----------
    [Fact]
    public void MaxDelayBelowBase_Fails()
    {
        var p = SshProfile();
        p.Retry.MaxDelayMs = 100;
        p.Retry.BaseDelayMs = 1000;
        Assert.False(_validator.Validate(p).IsValid);
    }

    [Fact]
    public void JitterAboveOne_Fails()
    {
        var p = SshProfile();
        p.Retry.JitterRatio = 1.5;
        Assert.False(_validator.Validate(p).IsValid);
    }

    [Fact]
    public void EmptyName_Fails()
    {
        var p = SshProfile();
        p.Name = " ";
        Assert.False(_validator.Validate(p).IsValid);
    }
}
