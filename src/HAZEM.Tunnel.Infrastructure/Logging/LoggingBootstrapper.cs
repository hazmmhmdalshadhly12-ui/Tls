using HAZEM.Tunnel.Core.Models;
using HAZEM.Tunnel.Core.Secrets;
using HAZEM.Tunnel.Infrastructure.Logging;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Infrastructure.Logging;

/// <summary>Wires the app's logging: rotating file + in-memory UI tail + console (debug builds).</summary>
public static class LoggingBootstrapper
{
    public static (ILoggerFactory Factory, RotatingFileLoggerProvider File, InMemoryLogProvider Memory)
        Create(AppSettings settings, SecretRedactor redactor, string? logDirectoryOverride = null)
    {
        var dir = logDirectoryOverride ?? AppPaths.Logs;
        var file = new RotatingFileLoggerProvider(
            dir, "hazem", settings.LogMaxFileSizeBytes, settings.LogRetentionDays, redactor,
            minLevel: LogLevel.Information);
        var memory = new InMemoryLogProvider();

        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Information);
            b.AddProvider(file);
            b.AddProvider(memory);
        });
        return (factory, file, memory);
    }
}
