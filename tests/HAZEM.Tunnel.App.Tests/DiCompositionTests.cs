using System.IO;
using HAZEM.Tunnel.App.Services;
using HAZEM.Tunnel.App.ViewModels;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Secrets;
using HAZEM.Tunnel.Infrastructure;
using HAZEM.Tunnel.Infrastructure.Configuration;
using HAZEM.Tunnel.Infrastructure.Credentials;
using HAZEM.Tunnel.Infrastructure.Logging;
using HAZEM.Tunnel.Infrastructure.Persistence;
using HAZEM.Tunnel.Networking.Diagnostics;
using HAZEM.Tunnel.Networking.Ssh;
using HAZEM.Tunnel.Networking.Tls;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;

namespace HAZEM.Tunnel.App.Tests;

/// <summary>
/// Guards the real DI composition root (AppComposition). The v1.1.0 release crashed at
/// startup with "Unable to resolve service for type 'ITunnelEngine' while attempting to
/// activate 'DashboardViewModel'" because ViewModels depend on interfaces while only the
/// concrete classes were registered — and none of the existing test projects ever built
/// the container. These tests build the REAL container (ValidateOnBuild + ValidateScopes)
/// and resolve every ViewModel, so a missing registration fails CI instead of shipping.
/// </summary>
public sealed class DiCompositionTests : IClassFixture<DiCompositionTests.CompositionFixture>
{
    private readonly CompositionFixture _fx;

    public DiCompositionTests(CompositionFixture fx) => _fx = fx;

    [Fact]
    public void Container_builds_with_validate_on_build()
    {
        // The fixture constructor already built the provider with ValidateOnBuild=true;
        // a broken registration would have thrown before any test ran.
        Assert.NotNull(_fx.Provider);
    }

    [Fact]
    public void MainViewModel_resolves_and_composes_every_page()
    {
        // MainViewModel's ctor sets SelectedPage → creates the first WPF view → must run
        // on an STA thread (as it does on the real UI thread) with the app's real
        // resource dictionaries loaded (views reference theme styles via StaticResource).
        var main = ResolveOnStaThread(() =>
        {
            EnsureAppResourcesLoaded();
            return _fx.Provider.GetRequiredService<MainViewModel>();
        });
        Assert.NotNull(main);
        // MainViewModel builds its page list from the child VMs — all pages must exist.
        Assert.True(main!.Pages.Count >= 8, $"Expected at least 8 pages, got {main.Pages.Count}.");
        Assert.NotNull(main.CurrentView);
    }

    [Fact]
    public void Every_viewmodel_resolves_from_the_real_container()
    {
        // MainViewModel is excluded here: its ctor instantiates the first WPF view and is
        // covered separately (on an STA thread) by MainViewModel_resolves_and_composes_every_page.
        Type[] viewModels =
        [
            typeof(DashboardViewModel),
            typeof(ServersViewModel),
            typeof(ProfilesViewModel),
            typeof(ProfileEditorViewModel),
            typeof(StatisticsViewModel),
            typeof(LogsViewModel),
            typeof(SettingsViewModel),
            typeof(TunnelSettingsViewModel),
            typeof(AboutViewModel),
        ];

        var failures = new List<string>();
        foreach (var vm in viewModels)
        {
            try
            {
                Assert.NotNull(_fx.Provider.GetRequiredService(vm));
            }
            catch (Exception ex)
            {
                failures.Add($"{vm.Name}: {ex.GetBaseException().Message}");
            }
        }

        Assert.True(failures.Count == 0,
            "DI resolution failed (this is exactly the v1.1.0 startup crash class):\n" +
            string.Join("\n", failures));
    }

    [Fact]
    public void Interface_aliases_resolve_to_one_shared_singleton()
    {
        // ITunnelEngine was the v1.1.0 crash — it must resolve through the interface.
        var engine1 = _fx.Provider.GetRequiredService<ITunnelEngine>();
        var engine2 = _fx.Provider.GetRequiredService<ITunnelEngine>();
        Assert.Same(engine1, engine2);

        Assert.Same(
            _fx.Provider.GetRequiredService<ITlsConnector>(),
            _fx.Provider.GetRequiredService<TlsConnector>());
        Assert.Same(
            _fx.Provider.GetRequiredService<ISshConnector>(),
            _fx.Provider.GetRequiredService<SshConnector>());
        Assert.Same(
            _fx.Provider.GetRequiredService<IHealthChecker>(),
            _fx.Provider.GetRequiredService<HealthChecker>());
        Assert.Same(
            _fx.Provider.GetRequiredService<IDialogService>(),
            _fx.Provider.GetRequiredService<DialogService>());
    }

    [Fact]
    public void Dashboard_and_statistics_share_the_same_engine_instance()
    {
        var dashboard = _fx.Provider.GetRequiredService<DashboardViewModel>();
        var statistics = _fx.Provider.GetRequiredService<StatisticsViewModel>();
        var engine = _fx.Provider.GetRequiredService<ITunnelEngine>();

        // Both pages observe the same engine — otherwise state/counter binding breaks silently.
        Assert.Same(engine, GetField(dashboard, "_engine"));
        Assert.Same(engine, GetField(statistics, "_engine"));
    }

    [Fact]
    public void InMemoryLogProvider_is_registered_for_the_logs_page()
    {
        // LogsViewModel depends on the InMemoryLogProvider instance created by the
        // LoggingBootstrapper; it was never registered before v1.2.0.
        Assert.Same(_fx.Memory, _fx.Provider.GetRequiredService<InMemoryLogProvider>());
    }

    [Fact]
    public void ILogger_flows_through_the_real_factory()
    {
        var logger = _fx.Provider.GetRequiredService<ILogger<DashboardViewModel>>();
        Assert.NotNull(logger);
        // The open generic must be wired to the app's factory (file + memory sinks),
        // not to a bare default factory.
        Assert.Same(_fx.LoggerFactory, _fx.Provider.GetRequiredService<ILoggerFactory>());
    }

    [Fact]
    public void Proxy_credential_checker_never_throws()
    {
        // The engine's SOCKS5 credential callback must answer false for unknown
        // credentials and must never throw — it runs on the proxy accept path.
        var engine = _fx.Provider.GetRequiredService<ITunnelEngine>();
        var checker = (Func<string, string, bool>?)engine.GetType()
            .GetField("_proxyCredentialChecker", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(engine);
        Assert.NotNull(checker);
        Assert.False(checker!("nobody", "wrong-password"));
    }

    private static object? GetField(object instance, string name) =>
        instance.GetType()
            .GetField(name, System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
            ?.GetValue(instance);

    /// <summary>Runs a resolution on a dedicated STA thread, like the real WPF UI thread.</summary>
    private static T ResolveOnStaThread<T>(Func<T> resolve) where T : class
    {
        T? result = null;
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try { result = resolve(); }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null) throw error;
        return result!;
    }

    /// <summary>
    /// Mirrors App.xaml: creates the WPF Application (if the test host has none) and
    /// merges the real DarkTheme dictionary, so view XAML resolves its StaticResources
    /// exactly as it does in production.
    /// </summary>
    private static void EnsureAppResourcesLoaded()
    {
        if (System.Windows.Application.Current is null)
        {
            _ = new System.Windows.Application
            {
                ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown
            };
        }
        var resources = System.Windows.Application.Current.Resources;
        if (resources.MergedDictionaries.Count == 0)
        {
            resources.MergedDictionaries.Add(new System.Windows.ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/HAZEM.Tunnel;component/Themes/DarkTheme.xaml")
            });
        }
    }

    /// <summary>
    /// Builds the real composition once for the whole fixture class — same sequence as
    /// App.OnStartup, with paths redirected to a temp directory.
    /// </summary>
    public sealed class CompositionFixture : IDisposable
    {
        private readonly RotatingFileLoggerProvider _file;

        public CompositionFixture()
        {
            TempRoot = Path.Combine(Path.GetTempPath(), "hazem-di-tests-" + Guid.NewGuid().ToString("N"));
            AppPaths.Initialize(TempRoot);

            var settings = new AppSettingsStore().Load();
            var redactor = new SecretRedactor();
            var (factory, file, memory) = LoggingBootstrapper.Create(settings, redactor);
            LoggerFactory = factory;
            _file = file;
            Memory = memory;

            var services = new ServiceCollection();
            AppComposition.ConfigureServices(services, redactor, LoggerFactory, Memory);
            Provider = AppComposition.BuildServiceProvider(services);

            // Warm-resolve every VM except MainViewModel: its ctor instantiates the first
            // WPF view and is exercised (on an STA thread) by its dedicated test.
            Provider.GetRequiredService<DashboardViewModel>();
            Provider.GetRequiredService<ServersViewModel>();
            Provider.GetRequiredService<ProfilesViewModel>();
            Provider.GetRequiredService<ProfileEditorViewModel>();
            Provider.GetRequiredService<StatisticsViewModel>();
            Provider.GetRequiredService<LogsViewModel>();
            Provider.GetRequiredService<SettingsViewModel>();
            Provider.GetRequiredService<TunnelSettingsViewModel>();
            Provider.GetRequiredService<AboutViewModel>();
        }

        public string TempRoot { get; }
        public ServiceProvider Provider { get; }
        public ILoggerFactory LoggerFactory { get; }
        public InMemoryLogProvider Memory { get; }

        public void Dispose()
        {
            try { Provider.Dispose(); } catch { /* best effort */ }
            try { LoggerFactory.Dispose(); } catch { /* best effort */ }
            try { _file.Dispose(); } catch { /* best effort */ }
            try { Directory.Delete(TempRoot, recursive: true); } catch { /* best effort */ }
        }
    }
}
