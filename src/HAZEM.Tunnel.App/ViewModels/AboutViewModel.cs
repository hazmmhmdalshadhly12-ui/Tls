using CommunityToolkit.Mvvm.ComponentModel;

namespace HAZEM.Tunnel.App.ViewModels;

/// <summary>About page content — honest scope, security notes, licensing.</summary>
public partial class AboutViewModel : ObservableObject
{
    public string AppName => "HAZEM Tunnel";
    public string Version =>
        typeof(AboutViewModel).Assembly.GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "1.2.0";

    public string Purpose =>
        "A Windows desktop client that routes your configured applications through an encrypted " +
        "tunnel (SSH, TLS/SNI, or WebSocket) and exposes a local SOCKS5 proxy on 127.0.0.1.";

    public string ScopeHonest =>
        "• Only applications configured to use the local SOCKS5 proxy are tunneled — there is no " +
        "system-wide VPN in this version (planned).\n" +
        "• \"Connected\" appears only after a real through-tunnel health check succeeds.\n" +
        "• All counters and rates come from real relayed bytes; nothing is simulated.\n" +
        "• A public hostname or SNI value never guarantees free or unrestricted access — that " +
        "depends entirely on your network operator and the servers you reach.";

    public string SecurityNotes =>
        "• TLS certificate validation is always on; it cannot be disabled in the UI or profiles.\n" +
        "• SSH host keys use TOFU/Strict/Pinned verification and are stored locally.\n" +
        "• Passwords, keys and passphrases live in the OS credential store (DPAPI), never in profile files.\n" +
        "• Logs redact secret-looking values and never record credentials or traffic contents.";

    public string License =>
        "MIT License. Third-party: SSH.NET (MIT), .NET runtime (MIT). " +
        "This build is NOT code-signed: Windows SmartScreen may warn on first run — see README for details.";
}
