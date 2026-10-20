using HAZEM.Tunnel.App.ViewModels;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Secrets;
using HAZEM.Tunnel.Core.Validation;
using HAZEM.Tunnel.Infrastructure.Configuration;
using HAZEM.Tunnel.Infrastructure.Credentials;
using HAZEM.Tunnel.Infrastructure.Logging;
using HAZEM.Tunnel.Infrastructure.Persistence;
using HAZEM.Tunnel.Networking;
using HAZEM.Tunnel.Networking.Diagnostics;
using HAZEM.Tunnel.Networking.Ssh;
using HAZEM.Tunnel.Networking.Tls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.App.Services;

/// <summary>
/// The single DI composition root for HAZEM Tunnel.
///
/// Extracted from App.xaml.cs in v1.2.0 so the composition is covered by an automated
/// test (tests/HAZEM.Tunnel.App.Tests/DiCompositionTests.cs). The v1.1.0 startup crash —
/// "Unable to resolve service for type 'ITunnelEngine' while attempting to activate
/// 'DashboardViewModel'" — was an untested composition root: ViewModels depend on
/// INTERFACES while only the CONCRETE classes were registered, and several services
/// (InMemoryLogProvider, ILoggerFactory, TunnelSettingsViewModel) were never registered
/// at all. Everything a ViewModel can ask for is registered here and asserted by the
/// DI composition tests, so a missing registration can never ship again.
/// </summary>
public static class AppComposition
{
    public static void ConfigureServices(
        IServiceCollection services,
        SecretRedactor redactor,
        ILoggerFactory loggerFactory,
        InMemoryLogProvider memoryLog)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(redactor);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(memoryLog);

        // ── Logging plumbing ────────────────────────────────────────────────
        // The REAL factory (rotating file + in-memory + redaction) is registered as the
        // container's ILoggerFactory, and the open generic ILogger<T> is mapped to
        // Logger<T>, so every ViewModel logger flows into the Logs page and the file.
        services.AddSingleton(loggerFactory);
        services.AddSingleton(typeof(ILogger<>), typeof(Logger<>));
        services.AddSingleton(memoryLog);

        // ── Infrastructure ──────────────────────────────────────────────────
        services.AddSingleton(redactor);
        services.AddSingleton<ICredentialStore>(sp => CredentialStoreFactory.Create());
        services.AddSingleton<IProfileStore>(sp => new JsonProfileStore(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<JsonProfileStore>()));
        services.AddSingleton<IKnownHostsStore>(sp => new KnownHostsFileStore());
        services.AddSingleton<IAppSettingsStore>(sp => new AppSettingsStore());

        // ── Core services (concrete implementations) ───────────────────────
        services.AddSingleton<ProfileValidator>();
        services.AddSingleton<ProfileImporter>();
        services.AddSingleton(sp => new SecretResolver(
            sp.GetRequiredService<ICredentialStore>(),
            sp.GetRequiredService<SecretRedactor>(),
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<SecretResolver>()));
        services.AddSingleton(sp => new SshHostKeyVerifier(sp.GetRequiredService<IKnownHostsStore>()));
        services.AddSingleton(sp => new TlsConnector(sp.GetRequiredService<ILogger<TlsConnector>>()));
        services.AddSingleton(sp => new SshConnector(
            sp.GetRequiredService<SshHostKeyVerifier>(),
            sp.GetRequiredService<SecretResolver>(),
            sp.GetRequiredService<ILogger<SshConnector>>()));
        services.AddSingleton(sp => new TransportFactory(
            sp.GetRequiredService<TlsConnector>(),
            sp.GetRequiredService<SshConnector>(),
            sp.GetRequiredService<SshHostKeyVerifier>(),
            sp.GetRequiredService<ILoggerFactory>()));
        services.AddSingleton(sp => new HealthChecker(sp.GetRequiredService<ILogger<HealthChecker>>()));

        // Tunnel engine: SOCKS5 credentials are resolved through the credential store and
        // compared in constant time; never logged.
        services.AddSingleton(sp => new TunnelEngine(
            sp.GetRequiredService<TransportFactory>(),
            sp.GetRequiredService<HealthChecker>(),
            sp.GetRequiredService<ILogger<TunnelEngine>>(),
            proxyCredentialChecker: (user, pass) => CheckProxyCredentials(sp, user, pass)));

        // ── Interface aliases ───────────────────────────────────────────────
        // ViewModels (Dashboard, Statistics, Servers) depend on the interfaces — NOT on
        // the concrete classes. Each alias forwards to the same shared singleton, so the
        // UI and the engine always observe one instance per service.
        services.AddSingleton<ITlsConnector>(sp => sp.GetRequiredService<TlsConnector>());
        services.AddSingleton<ISshConnector>(sp => sp.GetRequiredService<SshConnector>());
        services.AddSingleton<IHealthChecker>(sp => sp.GetRequiredService<HealthChecker>());
        services.AddSingleton<ITunnelEngine>(sp => sp.GetRequiredService<TunnelEngine>());

        // ── View models (every page MainViewModel composes, and their editor) ──
        services.AddSingleton<DialogService>();
        services.AddSingleton<IDialogService>(sp => sp.GetRequiredService<DialogService>());
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ServersViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<ProfileEditorViewModel>();
        services.AddSingleton<StatisticsViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<TunnelSettingsViewModel>();
        services.AddSingleton<AboutViewModel>();
    }

    /// <summary>
    /// Builds the container with <see cref="ServiceProviderOptions.ValidateOnBuild"/>, so
    /// a broken registration fails fast at startup — before any window is shown — instead
    /// of exploding mid-activation with a cryptic dialog.
    /// </summary>
    public static ServiceProvider BuildServiceProvider(IServiceCollection services) =>
        services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });

    private static bool CheckProxyCredentials(IServiceProvider sp, string user, string pass)
    {
        // The engine passes the SOCKS5 client-supplied credentials; compare with the stored
        // pair in constant time. Never logged.
        try
        {
            var store = sp.GetRequiredService<ICredentialStore>();
            var activeUser = store.GetSecret("active:socks-username");
            var activePass = store.GetSecret("active:socks-password");
            if (activeUser is null || activePass is null) return false;
            return FixedTimeEquals(activeUser, user) && FixedTimeEquals(activePass, pass);
        }
        catch
        {
            return false;
        }
    }

    private static bool FixedTimeEquals(string a, string b)
    {
        var ba = System.Text.Encoding.UTF8.GetBytes(a);
        var bb = System.Text.Encoding.UTF8.GetBytes(b);
        var len = Math.Max(ba.Length, bb.Length);
        var diff = ba.Length ^ bb.Length;
        for (var i = 0; i < len; i++)
            diff |= (i < ba.Length ? ba[i] : 0) ^ (i < bb.Length ? bb[i] : 0);
        return diff == 0;
    }
}
