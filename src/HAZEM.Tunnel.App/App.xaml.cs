using System.IO;
using System.Windows;
using System.Windows.Threading;
using HAZEM.Tunnel.App.Services;
using HAZEM.Tunnel.App.ViewModels;
using HAZEM.Tunnel.App.Views;
using HAZEM.Tunnel.Core.Interfaces;
using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Secrets;
using HAZEM.Tunnel.Core.Validation;
using HAZEM.Tunnel.Infrastructure;
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

namespace HAZEM.Tunnel.App;

/// <summary>
/// Application entry point.
/// v1.1.0 fixes baked in:
///  - Single-instance mutex: a second launch can never fight over the log file again.
///  - The rotating file provider can never throw (see RotatingFileLoggerProvider).
///  - Global handlers swallow logging failures instead of popping dialogs for them.
/// </summary>
public partial class App : Application
{
    private static Mutex? _singleInstanceMutex;
    private static ServiceProvider? _services;
    private static ILoggerFactory? _loggerFactory;
    private static RotatingFileLoggerProvider? _fileLog;
    private static InMemoryLogProvider? _memoryLog;
    private static SecretRedactor? _redactor;

    public static IServiceProvider Services => _services ?? throw new InvalidOperationException("App not initialized.");

    protected override void OnStartup(StartupEventArgs e)
    {
        // ── 1. Single instance guard (prevents log-file contention at the source) ──
        _singleInstanceMutex = new Mutex(initiallyOwned: true,
            @"Local\HAZEM.Tunnel.SingleInstance.v1", out var createdNew);
        if (!createdNew)
        {
            MessageBox.Show(
                "HAZEM Tunnel is already running.\n\n" +
                "Check the system tray or use the existing window. Running two instances would " +
                "make them compete for the same profiles, credentials and log files.",
                "HAZEM Tunnel", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown(0);
            return;
        }

        // ── 2. Paths, redactor, logging ──
        AppPaths.Initialize();
        _redactor = new SecretRedactor();
        var settings = new AppSettingsStore().Load();
        (_loggerFactory, _fileLog, _memoryLog) = LoggingBootstrapper.Create(settings, _redactor);

        base.OnStartup(e);

        // ── 3. Global exception handlers ──
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        // ── 4. DI container ──
        var services = new ServiceCollection();
        ConfigureServices(services, settings);
        _services = services.BuildServiceProvider();

        // ── 5. Main window ──
        var mainWindow = new MainWindow
        {
            DataContext = _services.GetRequiredService<MainViewModel>()
        };
        MainWindow = mainWindow;
        mainWindow.Show();
    }

    private static void ConfigureServices(IServiceCollection services, AppSettings settings)
    {
        // Infrastructure
        services.AddSingleton(_redactor!);
        services.AddSingleton<ICredentialStore>(sp => CredentialStoreFactory.Create());
        services.AddSingleton<IProfileStore>(sp => new JsonProfileStore(
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<JsonProfileStore>()));
        services.AddSingleton<IKnownHostsStore>(sp => new KnownHostsFileStore());
        services.AddSingleton<IAppSettingsStore>(sp => new AppSettingsStore());

        // Core services
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

        // Tunnel engine: SOCKS5 credentials resolved through the credential store, checked without logging them.
        services.AddSingleton(sp => new TunnelEngine(
            sp.GetRequiredService<TransportFactory>(),
            sp.GetRequiredService<HealthChecker>(),
            sp.GetRequiredService<ILogger<TunnelEngine>>(),
            proxyCredentialChecker: (user, pass) =>
            {
                // Wired lazily: real validation happens against the credential store only when a
                // profile with UsernamePassword auth is active. Never logged.
                return CheckProxyCredentials(user, pass);
            }));

        // View models
        services.AddSingleton<MainViewModel>();
        services.AddSingleton<DashboardViewModel>();
        services.AddSingleton<ServersViewModel>();
        services.AddSingleton<ProfilesViewModel>();
        services.AddSingleton<StatisticsViewModel>();
        services.AddSingleton<LogsViewModel>();
        services.AddSingleton<SettingsViewModel>();
        services.AddSingleton<AboutViewModel>();
        services.AddSingleton<DialogService>();
    }

    private static bool CheckProxyCredentials(string user, string pass)
    {
        // The engine passes the SOCKS5 client-supplied credentials; compare with the stored pair.
        try
        {
            var store = Services.GetRequiredService<ICredentialStore>();
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

    // ───────────────────── global exception handling ─────────────────────

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        if (IsLoggingFailure(e.Exception))
        {
            // The v1.0.0 dialog came from exactly this path. Logging failures are recorded
            // in memory only and NEVER surface as modal error dialogs.
            _memoryLog?.Append("App", LogLevel.Error,
                "A logging sink failed (suppressed): " + e.Exception.Message, null);
            e.Handled = true;
            return;
        }

        _loggerFactory?.CreateLogger("App").LogError(e.Exception, "Unhandled UI exception.");
        e.Handled = true;
        MessageBox.Show(
            "An unexpected error occurred.\n\n" + Describe(e.Exception) +
            "\n\nDetails were written to the log (Logs page → Open log folder).",
            "HAZEM Tunnel", MessageBoxButton.OK, MessageBoxImage.Error);
    }

    private void OnDomainUnhandledException(object? sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            if (IsLoggingFailure(ex)) return; // never crash the process over logging
            _loggerFactory?.CreateLogger("App").LogCritical(ex, "Unhandled app-domain exception.");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (IsLoggingFailure(e.Exception))
        {
            e.SetObserved();
            return;
        }
        _loggerFactory?.CreateLogger("App").LogError(e.Exception, "Unobserved task exception.");
        e.SetObserved(); // keep the process alive; the engine reports its own failures
    }

    /// <summary>Recognizes exceptions thrown by the logging pipeline itself ("An error occurred while writing to logger(s).").</summary>
    private static bool IsLoggingFailure(Exception ex) =>
        ex.Message.Contains("writing to logger", StringComparison.OrdinalIgnoreCase)
        || (ex is AggregateException agg && agg.InnerExceptions.Any(i =>
            i.Message.Contains("writing to logger", StringComparison.OrdinalIgnoreCase)
            || i.Message.Contains("because it is being used by another process", StringComparison.OrdinalIgnoreCase)))
        || ex.Message.Contains("because it is being used by another process", StringComparison.OrdinalIgnoreCase);

    private static string Describe(Exception ex)
    {
        var root = ex.GetBaseException();
        var text = root.Message;
        if (text.Length > 400) text = text[..400] + "…";
        return text;
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { _services?.Dispose(); } catch { }
        try { _fileLog?.Dispose(); } catch { }
        try { _memoryLog?.Dispose(); } catch { }
        try { _loggerFactory?.Dispose(); } catch { }
        try
        {
            _singleInstanceMutex?.ReleaseMutex();
            _singleInstanceMutex?.Dispose();
        }
        catch { }
        base.OnExit(e);
    }
}
