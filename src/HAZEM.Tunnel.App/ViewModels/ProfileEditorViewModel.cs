using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Validation;

namespace HAZEM.Tunnel.App.ViewModels;

/// <summary>
/// Profile editor VM: transport-aware field visibility, secret entry goes straight
/// to the OS credential store, and validation runs live before Save is allowed.
/// </summary>
public partial class ProfileEditorViewModel : ObservableObject
{
    private readonly ICredentialStore _credentials;

    public ProfileEditorViewModel(ICredentialStore credentials)
    {
        _credentials = credentials;
        TransportKinds = Enum.GetValues<TransportKind>();
        SshAuthModes = Enum.GetValues<SshAuthMode>();
        HostKeyPolicies = Enum.GetValues<SshHostKeyPolicy>();
        ProxyAuthModes = Enum.GetValues<ProxyAuthMode>();
    }

    public TunnelProfile? Editing { get; set; }
    public bool IsNew { get; set; }

    public IReadOnlyList<TransportKind> TransportKinds { get; }
    public IReadOnlyList<SshAuthMode> SshAuthModes { get; }
    public IReadOnlyList<SshHostKeyPolicy> HostKeyPolicies { get; }
    public IReadOnlyList<ProxyAuthMode> ProxyAuthModes { get; }

    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private TransportKind _transport = TransportKind.Ssh;

    // TLS group
    [ObservableProperty] private string _tlsHost = string.Empty;
    [ObservableProperty] private int _tlsPort = 443;
    [ObservableProperty] private string _tlsSni = string.Empty;
    [ObservableProperty] private string _trustedRootPem = string.Empty;

    // SSH group
    [ObservableProperty] private string _sshHost = string.Empty;
    [ObservableProperty] private int _sshPort = 22;
    [ObservableProperty] private string _sshUsername = string.Empty;
    [ObservableProperty] private SshAuthMode _sshAuth = SshAuthMode.Password;
    [ObservableProperty] private SshHostKeyPolicy _hostKeyPolicy = SshHostKeyPolicy.TrustOnFirstUse;
    [ObservableProperty] private string _pinnedFingerprint = string.Empty;
    [ObservableProperty] private string _forwardHost = string.Empty;
    [ObservableProperty] private int _forwardPort = 80;
    [ObservableProperty] private bool _hasPassword;      // secret already stored?
    [ObservableProperty] private bool _hasPrivateKey;
    [ObservableProperty] private bool _hasPassphrase;

    // WebSocket group
    [ObservableProperty] private string _wsUrl = string.Empty;
    [ObservableProperty] private string _wsHeadersText = string.Empty;

    // Proxy group
    [ObservableProperty] private string _bindAddress = "127.0.0.1";
    [ObservableProperty] private int _proxyPort = 1080;
    [ObservableProperty] private bool _allowLan;
    [ObservableProperty] private ProxyAuthMode _proxyAuth = ProxyAuthMode.None;
    [ObservableProperty] private bool _hasProxyUsername;
    [ObservableProperty] private bool _hasProxyPassword;

    // Retry
    [ObservableProperty] private int _maxAttempts = 5;
    [ObservableProperty] private int _baseDelayMs = 1000;
    [ObservableProperty] private int _maxDelayMs = 30000;

    // Live validation
    [ObservableProperty] private string _validationText = string.Empty;
    [ObservableProperty] private bool _isValid;

    partial void OnTransportChanged(TransportKind value) => RefreshVisibility();
    partial void OnSshAuthChanged(SshAuthMode value) => RefreshVisibility();
    partial void OnProxyAuthChanged(ProxyAuthMode value) => RefreshVisibility();

    public bool ShowTls => Transport is TransportKind.TlsDirect or TransportKind.TlsSsh;
    public bool ShowSsh => Transport is TransportKind.Ssh or TransportKind.TlsSsh;
    public bool ShowWs => Transport == TransportKind.WebSocket;
    public bool ShowSshPassword => ShowSsh && SshAuth == SshAuthMode.Password;
    public bool ShowSshKey => ShowSsh && SshAuth == SshAuthMode.PrivateKey;
    public bool ShowPinned => ShowSsh && HostKeyPolicy == SshHostKeyPolicy.Pinned;
    public bool ShowProxyAuth => ProxyAuth == ProxyAuthMode.UsernamePassword;

    private void RefreshVisibility()
    {
        OnPropertyChanged(nameof(ShowTls));
        OnPropertyChanged(nameof(ShowSsh));
        OnPropertyChanged(nameof(ShowWs));
        OnPropertyChanged(nameof(ShowSshPassword));
        OnPropertyChanged(nameof(ShowSshKey));
        OnPropertyChanged(nameof(ShowPinned));
        OnPropertyChanged(nameof(ShowProxyAuth));
    }

    /// <summary>Copies values from the profile into the editor fields (called by the dialog on open).</summary>
    public void LoadFromProfile()
    {
        var p = Editing;
        if (p is null) return;
        Name = p.Name;
        Transport = p.TransportKind;
        TlsHost = p.Tls.Host;
        TlsPort = p.Tls.Port;
        TlsSni = p.Tls.Sni;
        TrustedRootPem = p.Tls.TrustedRootPem ?? string.Empty;
        SshHost = p.Ssh.Host;
        SshPort = p.Ssh.Port;
        SshUsername = p.Ssh.Username;
        SshAuth = p.Ssh.AuthMode;
        HostKeyPolicy = p.Ssh.HostKeyPolicy;
        PinnedFingerprint = p.Ssh.PinnedHostKeyFingerprint ?? string.Empty;
        ForwardHost = p.Ssh.RemoteForwardHost;
        ForwardPort = p.Ssh.RemoteForwardPort;
        WsUrl = p.WebSocket.Url;
        WsHeadersText = string.Join(Environment.NewLine,
            p.WebSocket.Headers.Select(kv => $"{kv.Key}: {kv.Value}"));
        BindAddress = p.LocalProxy.BindAddress;
        ProxyPort = p.LocalProxy.Port;
        AllowLan = p.LocalProxy.AllowLan;
        ProxyAuth = p.LocalProxy.AuthMode;
        MaxAttempts = p.Retry.MaxAttempts;
        BaseDelayMs = p.Retry.BaseDelayMs;
        MaxDelayMs = p.Retry.MaxDelayMs;
        RefreshStoredSecretFlags();
        ValidateNow();
    }

    private void RefreshStoredSecretFlags()
    {
        var p = Editing;
        if (p is null) return;
        HasPassword = p.Ssh.PasswordRef is not null && _credentials.GetSecret(p.Ssh.PasswordRef.Key) is not null;
        HasPrivateKey = p.Ssh.PrivateKeyRef is not null && _credentials.GetSecret(p.Ssh.PrivateKeyRef.Key) is not null;
        HasPassphrase = p.Ssh.PassphraseRef is not null && _credentials.GetSecret(p.Ssh.PassphraseRef.Key) is not null;
        HasProxyUsername = p.LocalProxy.UsernameRef is not null && _credentials.GetSecret(p.LocalProxy.UsernameRef.Key) is not null;
        HasProxyPassword = p.LocalProxy.PasswordRef is not null && _credentials.GetSecret(p.LocalProxy.PasswordRef.Key) is not null;
    }

    // ---- secret entry (direct to credential store; never into profiles/logs) ----
    public void SetSshPassword(string plaintext)
    {
        var p = Editing!;
        p.Ssh.PasswordRef ??= new SecretRef($"profile:{p.Id}:ssh-password");
        _credentials.SetSecret(p.Ssh.PasswordRef.Key, plaintext);
        HasPassword = true;
    }

    public void SetSshPrivateKey(string pem)
    {
        var p = Editing!;
        p.Ssh.PrivateKeyRef ??= new SecretRef($"profile:{p.Id}:ssh-private-key");
        _credentials.SetSecret(p.Ssh.PrivateKeyRef.Key, pem);
        HasPrivateKey = true;
    }

    public void SetSshPassphrase(string plaintext)
    {
        var p = Editing!;
        p.Ssh.PassphraseRef ??= new SecretRef($"profile:{p.Id}:ssh-passphrase");
        _credentials.SetSecret(p.Ssh.PassphraseRef.Key, plaintext);
        HasPassphrase = true;
    }

    public void SetProxyUsername(string plaintext)
    {
        var p = Editing!;
        p.LocalProxy.UsernameRef ??= new SecretRef($"profile:{p.Id}:socks-username");
        _credentials.SetSecret(p.LocalProxy.UsernameRef.Key, plaintext);
        HasProxyUsername = true;
    }

    public void SetProxyPassword(string plaintext)
    {
        var p = Editing!;
        p.LocalProxy.PasswordRef ??= new SecretRef($"profile:{p.Id}:socks-password");
        _credentials.SetSecret(p.LocalProxy.PasswordRef.Key, plaintext);
        HasProxyPassword = true;
    }

    [RelayCommand]
    private void ValidateNow()
    {
        var candidate = Build();
        if (candidate is null) { IsValid = false; ValidationText = "Fix the fields above first."; return; }
        var result = new ProfileValidator().Validate(candidate);
        IsValid = result.IsValid;
        ValidationText = result.IsValid
            ? "✔ Profile is valid and ready to save."
            : result.Describe();
    }

    /// <summary>Builds a profile from the editor fields; returns null if basic fields are missing.</summary>
    public TunnelProfile? Build()
    {
        var p = Editing;
        if (p is null) return null;
        p.Name = Name.Trim();
        p.TransportKind = Transport;
        p.Tls.Host = TlsHost.Trim();
        p.Tls.Port = TlsPort;
        p.Tls.Sni = TlsSni.Trim();
        p.Tls.TrustedRootPem = string.IsNullOrWhiteSpace(TrustedRootPem) ? null : TrustedRootPem;
        p.Ssh.Host = SshHost.Trim();
        p.Ssh.Port = SshPort;
        p.Ssh.Username = SshUsername.Trim();
        p.Ssh.AuthMode = SshAuth;
        p.Ssh.HostKeyPolicy = HostKeyPolicy;
        p.Ssh.PinnedHostKeyFingerprint = string.IsNullOrWhiteSpace(PinnedFingerprint) ? null : PinnedFingerprint.Trim();
        p.Ssh.RemoteForwardHost = ForwardHost.Trim();
        p.Ssh.RemoteForwardPort = ForwardPort;
        p.WebSocket.Url = WsUrl.Trim();
        p.WebSocket.Headers.Clear();
        foreach (var line in WsHeadersText.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var idx = line.IndexOf(':');
            if (idx > 0)
                p.WebSocket.Headers[line[..idx].Trim()] = line[(idx + 1)..].Trim();
        }
        p.LocalProxy.BindAddress = BindAddress.Trim();
        p.LocalProxy.Port = ProxyPort;
        p.LocalProxy.AllowLan = AllowLan;
        p.LocalProxy.AuthMode = ProxyAuth;
        p.Retry.MaxAttempts = MaxAttempts;
        p.Retry.BaseDelayMs = BaseDelayMs;
        p.Retry.MaxDelayMs = MaxDelayMs;
        return p;
    }
}
