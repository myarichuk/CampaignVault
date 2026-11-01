using System.Collections.Concurrent;
using System.Globalization;

namespace CampaignVault.Hosting;

/// <summary>
/// Appends log lines to a daily file (<c>campaignvault-yyyyMMdd.log</c>) so a play session can be reviewed after the
/// console that ran the server is gone. Files older than <see cref="RetentionDays"/> are deleted at startup.
/// One writer per process; every line is flushed, since the point is to survive a crash or a closed terminal.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    public const int RetentionDays = 14;
    private const string FilePrefix = "campaignvault-";

    private readonly string _directory;
    private readonly Func<DateTime> _now;
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private StreamWriter? _writer;
    private DateOnly _writerDay;

    public FileLoggerProvider(string directory, Func<DateTime>? now = null)
    {
        _directory = directory;
        _now = now ?? (() => DateTime.Now);
        Directory.CreateDirectory(directory);
        DeleteExpiredFiles();
    }

    /// <summary>
    /// CAMPAIGN_LOG_DIR picks the folder; "off" disables file logging. Unset means a <c>logs</c> folder next to the binary.
    /// </summary>
    public static string? ResolveDirectory(string? setting, string baseDirectory) => setting?.Trim() switch
    {
        null or "" => Path.Combine(baseDirectory, "logs"),
        var s when s.Equals("off", StringComparison.OrdinalIgnoreCase) => null,
        var s => s,
    };

    public ILogger CreateLogger(string categoryName) => _loggers.GetOrAdd(categoryName, name => new FileLogger(this, name));

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }

    private void Write(string category, LogLevel level, string message, Exception? exception)
    {
        var now = _now();
        var line = string.Create(CultureInfo.InvariantCulture,
            $"{now:yyyy-MM-dd HH:mm:ss.fff} [{Abbreviate(level)}] {category}: {message}");
        lock (_gate)
        {
            try
            {
                var writer = WriterFor(DateOnly.FromDateTime(now));
                writer.WriteLine(line);
                if (exception is not null)
                {
                    writer.WriteLine(exception.ToString());
                }
            }
            catch (IOException)
            {
                // Logging must never take the server down; a full disk or locked file just loses lines.
            }
        }
    }

    private StreamWriter WriterFor(DateOnly day)
    {
        if (_writer is not null && _writerDay == day)
        {
            return _writer;
        }

        _writer?.Dispose();
        var path = Path.Combine(_directory, $"{FilePrefix}{day:yyyyMMdd}.log");
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream) { AutoFlush = true };
        _writerDay = day;
        return _writer;
    }

    private void DeleteExpiredFiles()
    {
        var cutoff = _now().AddDays(-RetentionDays);
        foreach (var file in Directory.EnumerateFiles(_directory, $"{FilePrefix}*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Another process holding an old file is not worth failing startup over.
            }
        }
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "trce",
        LogLevel.Debug => "dbug",
        LogLevel.Information => "info",
        LogLevel.Warning => "warn",
        LogLevel.Error => "fail",
        LogLevel.Critical => "crit",
        _ => "none",
    };

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel))
            {
                provider.Write(category, logLevel, formatter(state, exception), exception);
            }
        }
    }
}
