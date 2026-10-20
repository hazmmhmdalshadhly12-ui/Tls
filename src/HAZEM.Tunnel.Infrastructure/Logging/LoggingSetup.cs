using System.IO;
using System.Text;
using HAZEM.Tunnel.Core.Secrets;
using Microsoft.Extensions.Logging;

namespace HAZEM.Tunnel.Infrastructure.Logging;

/// <summary>
/// ═══════════════════════════════════════════════════════════════════════════
///  THE LOGGING GOLDEN RULE (v1.1.0 fix): A LOGGER MUST NEVER THROW.
/// ═══════════════════════════════════════════════════════════════════════════
/// The v1.0.0 error dialog ("The process cannot access the file … because it is
/// being used by another process") happened when the log file was briefly held
/// by another process (second app instance, antivirus scan, log viewer) and the
/// provider threw during rotation/reopen. That exception propagated out of the
/// logging pipeline into the global handler, which popped an error dialog.
///
/// This provider is hardening against ALL of that:
///  1. Every file I/O runs inside try/catch — failures degrade silently.
///  2. ONE persistent FileStream per provider, opened with FileShare.Read so
///     external readers (editors, Get-Content) can read concurrently.
///  3. Rotation happens under the same lock as writes; reopen retries briefly;
///     if the file is still locked we keep writing to a sidecar file instead of
///     throwing, and retry the primary file on later writes.
///  4. Dispose is exception-safe (must not break app shutdown).
/// </summary>
public sealed class RotatingFileLoggerProvider : ILoggerProvider
{
    private readonly string _logDirectory;
    private readonly string _baseName;
    private readonly long _maxFileSizeBytes;
    private readonly int _retentionDays;
    private readonly SecretRedactor _redactor;
    private readonly LogLevel _minLevel;
    private readonly object _gate = new();
    private readonly string[] _categoryExclusions = ["Microsoft.Hosting.Lifetime"];

    private FileStream? _stream;
    private StreamWriter? _writer;
    private DateTime _currentDate = DateTime.MinValue;
    private int _currentSeq;             // current file sequence within the day (size rotation)
    private long _currentSize;
    private string _currentPath = string.Empty;
    private string? _sidecarPath;         // used when the primary file is locked
    private FileStream? _sidecarStream;
    private StreamWriter? _sidecarWriter;
    private long _sidecarSize;
    private bool _disposed;

    public RotatingFileLoggerProvider(string logDirectory, string baseName,
        long maxFileSizeBytes, int retentionDays, SecretRedactor redactor,
        LogLevel minLevel = LogLevel.Information)
    {
        _logDirectory = logDirectory;
        _baseName = baseName;
        _maxFileSizeBytes = Math.Max(256 * 1024, maxFileSizeBytes);
        _retentionDays = Math.Max(1, retentionDays);
        _redactor = redactor;
        _minLevel = minLevel;
        try { Directory.CreateDirectory(logDirectory); } catch { /* degrade */ }
        CleanupOldLogs(); // exception-safe by design
    }

    public ILogger CreateLogger(string categoryName) =>
        new RotatingFileLogger(this, categoryName);

    // ─────────────────────────── writing ───────────────────────────

    internal void Write(string categoryName, LogLevel level, string message, Exception? exception)
    {
        if (_disposed) return;
        if (level < _minLevel && level != LogLevel.Critical) return;
        if (_categoryExclusions.Any(categoryName.StartsWith)) return;

        var line = Format(categoryName, level, message, exception);
        lock (_gate)
        {
            if (_disposed) return;

            // Fast path: healthy primary file.
            if (EnsureWriter_Locked())
            {
                try
                {
                    _writer!.WriteLine(line);
                    _writer.Flush();
                    _currentSize += line.Length + Environment.NewLine.Length;
                    if (_currentSize >= _maxFileSizeBytes) RotateSize_Locked();
                    return;
                }
                catch (IOException)
                {
                    // File suddenly locked (AV scan, rotation race, disk hiccup):
                    // fall through to the sidecar strategy. NEVER rethrow.
                    SafeTearDownPrimary_Locked();
                }
                catch (ObjectDisposedException)
                {
                    SafeTearDownPrimary_Locked();
                }
                catch
                {
                    return; // unknown failure — drop this line, stay alive.
                }
            }

            // Degraded path: primary unavailable → sidecar (or memory-only last resort).
            try
            {
                EnsureSidecarWriter_Locked();
                _sidecarWriter?.WriteLine(line);
                _sidecarWriter?.Flush();
                _sidecarSize += line.Length + Environment.NewLine.Length;
            }
            catch
            {
                // Even the sidecar failed (disk full?). Drop silently — logging must never crash the app.
            }
        }
    }

    private string Format(string category, LogLevel level, string message, Exception? ex)
    {
        var sb = new StringBuilder(256);
        sb.Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
          .Append(' ').Append(LevelTag(level))
          .Append(" [").Append(ShortCategory(category)).Append("] ")
          .Append(_redactor.Redact(message));
        if (ex is not null)
        {
            sb.AppendLine().Append("    Exception: ").Append(ex.GetType().Name)
              .Append(": ").Append(_redactor.Redact(ex.Message));
            if (ex.StackTrace is { } stack)
            {
                foreach (var frame in stack.Split('\n').Take(12))
                    sb.Append("    ").Append(_redactor.Redact(frame.TrimEnd()));
            }
        }
        return sb.ToString();
    }

    private static string LevelTag(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRACE",
        LogLevel.Debug => "DEBUG",
        LogLevel.Information => "INFO ",
        LogLevel.Warning => "WARN ",
        LogLevel.Error => "ERROR",
        LogLevel.Critical => "FATAL",
        _ => "-----"
    };

    private static string ShortCategory(string category)
    {
        const string prefix = "HAZEM.Tunnel.";
        return category.StartsWith(prefix, StringComparison.Ordinal) ? category[prefix.Length..] : category;
    }

    // ─────────────────────── file lifecycle (all under _gate) ───────────────────────

    private string PathFor(DateTime date, int seq) =>
        Path.Combine(_logDirectory, $"{_baseName}-{date:yyyyMMdd}{(seq > 0 ? $"-{seq}" : "")}.log");

    private bool EnsureWriter_Locked()
    {
        var today = DateTime.Today;
        if (_writer is not null && _currentDate == today) return true;

        // Date rolled over (or first write): reset sequence.
        SafeTearDownPrimary_Locked();
        if (_currentDate != today)
        {
            _currentDate = today;
            _currentSeq = 0;
        }

        var seq = _currentSeq;
        var failures = 0;
        while (failures <= 9)
        {
            _currentPath = PathFor(today, seq);
            try
            {
                _stream = new FileStream(_currentPath, FileMode.Append, FileAccess.Write, FileShare.Read);
                _writer = new StreamWriter(_stream, Encoding.UTF8) { AutoFlush = false };
                try { _currentSize = _stream.Length; }
                catch (IOException) { _currentSize = 0; }
                _currentSeq = seq;
                return true;
            }
            catch (IOException)
            {
                // Locked by another process right now (scanner, second instance, log viewer):
                // try the next sequence number for today; give up after a bounded window.
                seq++;
                failures++;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    private void RotateSize_Locked()
    {
        SafeTearDownPrimary_Locked();
        _currentSeq++;          // next EnsureWriter_Locked opens the next sequence file for the same day
        _sidecarPath = null;
    }

    private void SafeTearDownPrimary_Locked()
    {
        try { _writer?.Flush(); } catch { }
        try { _writer?.Dispose(); } catch { }
        try { _stream?.Dispose(); } catch { }
        _writer = null;
        _stream = null;
    }

    private void EnsureSidecarWriter_Locked()
    {
        if (_sidecarWriter is not null) return;
        var suffix = 0;
        while (true)
        {
            var candidate = Path.Combine(_logDirectory, $"{_baseName}-{DateTime.Today:yyyyMMdd}-locked{(suffix > 0 ? $"-{suffix}" : "")}.log");
            try
            {
                _sidecarStream = new FileStream(candidate, FileMode.Append, FileAccess.Write, FileShare.Read);
                _sidecarWriter = new StreamWriter(_sidecarStream, Encoding.UTF8) { AutoFlush = true };
                _sidecarPath = candidate;
                return;
            }
            catch (IOException)
            {
                suffix++;
                if (suffix > 9) return; // give up for this write
            }
            catch
            {
                return;
            }
        }
    }

    private void CleanupOldLogs()
    {
        try
        {
            if (!Directory.Exists(_logDirectory)) return;
            var cutoff = DateTime.UtcNow.AddDays(-_retentionDays);
            foreach (var file in Directory.EnumerateFiles(_logDirectory, $"{_baseName}-*.log"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(file) < cutoff)
                        File.Delete(file);
                }
                catch (IOException) { /* in use — try again next startup */ }
                catch (UnauthorizedAccessException) { /* skip */ }
            }
        }
        catch
        {
            // Retention cleanup is best-effort by definition.
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            SafeTearDownPrimary_Locked();
            try { _sidecarWriter?.Flush(); } catch { }
            try { _sidecarWriter?.Dispose(); } catch { }
            try { _sidecarStream?.Dispose(); } catch { }
            _sidecarWriter = null;
            _sidecarStream = null;
        }
    }
}

/// <summary>ILogger adapter; CreateLogger allocates one lightweight object per category.</summary>
internal sealed class RotatingFileLogger : ILogger
{
    private readonly RotatingFileLoggerProvider _provider;
    private readonly string _category;

    public RotatingFileLogger(RotatingFileLoggerProvider provider, string category)
    {
        _provider = provider;
        _category = category;
    }

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        // Two layers of protection: the provider never throws, and this adapter swallows too.
        try
        {
            _provider.Write(_category, logLevel, formatter(state, exception), exception);
        }
        catch
        {
            // Swallow everything — a logging failure must never take the app down.
        }
    }
}
