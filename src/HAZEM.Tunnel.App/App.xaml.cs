using System.Windows;
using System.Windows.Threading;
using HAZEM.Tunnel.App.Services;
using HAZEM.Tunnel.App.ViewModels;
using HAZEM.Tunnel.App.Views;
using HAZEM.Tunnel.Core.Secrets;
using HAZEM.Tunnel.Infrastructure;
using HAZEM.Tunnel.Infrastructure.Configuration;
using HAZEM.Tunnel.Infrastructure.Logging;
using HAZEM.Tunnel.Infrastructure.Persistence;
using HAZEM.Tunnel.Networking.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.App;

/// <summary>
/// Application entry point.
/// v1.2.0 fixes baked in:
///  - The DI composition root was extracted to AppComposition and is covered by automated
///    tests — the v1.1.0 startup crash (missing ITunnelEngine registration) can not ship again.
///  - The container is built with ValidateOnBuild; a broken registration shows a friendly
///    dialog with the log folder and exits cleanly instead of a cryptic activation error.
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
        // Built with ValidateOnBuild: a broken registration fails HERE — before any window
        // is shown — with a friendly dialog that points at the log folder, instead of a
        // cryptic activation error mid-startup (the v1.1.0 ITunnelEngine crash).
        try
        {
            var services = new ServiceCollection();
            AppComposition.ConfigureServices(services, _redactor!, _loggerFactory!, _memoryLog!);
            _services = AppComposition.BuildServiceProvider(services);
        }
        catch (Exception ex)
        {
            ShowFatalStartupError(ex, "building the application services");
            return;
        }

        // ── 5. Main window ──
        try
        {
            var mainWindow = new MainWindow
            {
                DataContext = _services.GetRequiredService<MainViewModel>()
            };
            MainWindow = mainWindow;
            mainWindow.Show();
        }
        catch (Exception ex)
        {
            ShowFatalStartupError(ex, "opening the main window");
        }
    }

    /// <summary>
    /// Startup must never die silently or half-start. Logs the failure, shows a plain
    /// message with the log location, opens the log folder, and exits cleanly.
    /// </summary>
    private void ShowFatalStartupError(Exception ex, string while_)
    {
        try { _loggerFactory?.CreateLogger("App").LogCritical(ex, "Startup failed while {0}.", while_); } catch { }
        var logDir = AppPaths.Logs;
        var message =
            "HAZEM Tunnel could not start (" + while_ + ").\n\n" +
            Describe(ex) + "\n\n" +
            "The full details were written to the log:\n" + logDir + "\n\n" +
            "Please include the latest hazem-*.log file if you report this problem.";
        try
        {
            MessageBox.Show(message, "HAZEM Tunnel — startup problem",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* a message box failing must not crash the shutdown path */ }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = logDir,
                UseShellExecute = true
            });
        }
        catch { /* explorer may be unavailable; not fatal */ }
        Shutdown(1);
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
