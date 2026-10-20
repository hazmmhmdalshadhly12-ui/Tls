using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HAZEM.Tunnel.App.Services;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Networking;
using HAZEM.Tunnel.Networking.Ssh;

namespace HAZEM.Tunnel.App.ViewModels;

/// <summary>Servers page: SSH reachability tester + known-hosts manager.</summary>
public partial class ServersViewModel : ObservableObject
{
    private readonly ISshConnector _ssh;
    private readonly IKnownHostsStore _knownHosts;
    private readonly ICredentialStore _credentials;
    private readonly IDialogService _dialogs;
    private const string TestSecretKey = "transient:ssh-test-password";

    public ServersViewModel(ISshConnector ssh, IKnownHostsStore knownHosts,
        ICredentialStore credentials, IDialogService dialogs)
    {
        _ssh = ssh;
        _knownHosts = knownHosts;
        _credentials = credentials;
        _dialogs = dialogs;
        ReloadKnownHosts();
    }

    // ---- tester ----
    [ObservableProperty] private string _testHost = "example.com";
    [ObservableProperty] private int _testPort = 22;
    [ObservableProperty] private string _testUsername = string.Empty;
    /// <summary>Transient password, bound from code-behind (PasswordBox). Never persisted; cleared after the test.</summary>
    [ObservableProperty] private string _testPassword = string.Empty;
    [ObservableProperty] private string _testResult = "Run a test to see the result here.";
    [ObservableProperty] private bool _testing;

    partial void OnTestingChanged(bool value) => OnPropertyChanged(nameof(CanTest));
    public bool CanTest => !Testing;

    [RelayCommand]
    private async Task TestAsync()
    {
        if (string.IsNullOrWhiteSpace(TestHost) || string.IsNullOrWhiteSpace(TestUsername))
        {
            _dialogs.Error("SSH test", "Host and username are required.");
            return;
        }
        Testing = true;
        TestResult = "Testing…";
        try
        {
            // Transient credential: stored only for the duration of the test, removed right after.
            _credentials.SetSecret(TestSecretKey, TestPassword ?? string.Empty);
            var options = new SshOptions
            {
                Host = TestHost.Trim(),
                Port = TestPort,
                Username = TestUsername.Trim(),
                AuthMode = SshAuthMode.Password,
                PasswordRef = new SecretRef(TestSecretKey)
            };
            var result = await _ssh.TestAsync(options, CancellationToken.None);
            TestResult = result.Success
                ? $"✔ {result.Message} ({result.Elapsed.TotalMilliseconds:0} ms)"
                : $"✘ {result.Message}";
        }
        finally
        {
            _credentials.RemoveSecret(TestSecretKey);
            TestPassword = string.Empty;
            Testing = false;
        }
    }

    // ---- known hosts ----
    public ObservableCollection<KnownHostEntry> KnownHosts { get; } = new();

    private void ReloadKnownHosts()
    {
        KnownHosts.Clear();
        foreach (var h in _knownHosts.All()) KnownHosts.Add(h);
    }

    [RelayCommand]
    private void RemoveKnownHost(KnownHostEntry? entry)
    {
        if (entry is null) return;
        if (!_dialogs.Confirm("Remove known host",
                $"Remove the stored host key for {entry.Host}:{entry.Port}?\n\n" +
                "The next connection will treat it as a first use (TOFU) again.")) return;
        _knownHosts.Remove(entry.Host, entry.Port);
        ReloadKnownHosts();
    }

    [RelayCommand]
    private void RefreshKnownHosts() => ReloadKnownHosts();
}
