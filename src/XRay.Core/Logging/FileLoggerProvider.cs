using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.Extensions.Logging;

namespace XRay.Core.Logging;

/// <summary>
/// Minimal file logger: one file per day (<c>xray-yyyyMMdd.log</c>), rolled over at a size limit,
/// with old files deleted after a retention period. Writes happen on a background thread.
/// </summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly BlockingCollection<string> _queue = new(boundedCapacity: 10_000);
    private readonly Thread _writer;
    private readonly string _directory;
    private readonly long _maxFileBytes;
    private readonly LogLevel _minimumLevel;
    private StreamWriter? _stream;
    private string? _currentPath;

    public FileLoggerProvider(string directory, LogLevel minimumLevel = LogLevel.Information, int retainDays = 14, long maxFileBytes = 5 * 1024 * 1024)
    {
        _directory = directory;
        _minimumLevel = minimumLevel;
        _maxFileBytes = maxFileBytes;
        Directory.CreateDirectory(directory);
        DeleteOldFiles(retainDays);
        _writer = new Thread(WriteLoop) { IsBackground = true, Name = "X-RAY log writer" };
        _writer.Start();
    }

    public ILogger CreateLogger(string categoryName) => new FileLogger(this, categoryName);

    public void Dispose()
    {
        _queue.CompleteAdding();
        _writer.Join(TimeSpan.FromSeconds(2));
        _stream?.Dispose();
        _queue.Dispose();
    }

    private void Enqueue(string line)
    {
        if (!_queue.IsAddingCompleted)
        {
            // Drop messages rather than block the caller if the disk cannot keep up.
            _queue.TryAdd(line);
        }
    }

    private void WriteLoop()
    {
        try
        {
            foreach (string line in _queue.GetConsumingEnumerable())
            {
                try
                {
                    EnsureStream();
                    _stream!.WriteLine(line);
                    if (_queue.Count == 0)
                    {
                        _stream.Flush();
                    }
                }
                catch (IOException)
                {
                    // Logging must never crash the app.
                    _stream?.Dispose();
                    _stream = null;
                }
            }
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void EnsureStream()
    {
        string basePath = Path.Combine(_directory, $"xray-{DateTime.Now:yyyyMMdd}");
        if (_stream is not null && _currentPath is not null && _currentPath.StartsWith(basePath, StringComparison.Ordinal) &&
            _stream.BaseStream.Length < _maxFileBytes)
        {
            return;
        }

        _stream?.Dispose();
        string path = basePath + ".log";
        for (int i = 1; File.Exists(path) && new FileInfo(path).Length >= _maxFileBytes; i++)
        {
            path = $"{basePath}.{i}.log";
        }

        _currentPath = path;
        _stream = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read), Encoding.UTF8);
    }

    private void DeleteOldFiles(int retainDays)
    {
        DateTime cutoff = DateTime.Now.AddDays(-retainDays);
        foreach (string file in Directory.EnumerateFiles(_directory, "xray-*.log"))
        {
            try
            {
                if (File.GetLastWriteTime(file) < cutoff)
                {
                    File.Delete(file);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        private readonly string _shortCategory = category[(category.LastIndexOf('.') + 1)..];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel >= provider._minimumLevel && logLevel != LogLevel.None;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            string level = logLevel switch
            {
                LogLevel.Trace => "TRC",
                LogLevel.Debug => "DBG",
                LogLevel.Information => "INF",
                LogLevel.Warning => "WRN",
                LogLevel.Error => "ERR",
                _ => "CRT",
            };
            var sb = new StringBuilder(128);
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture))
              .Append(" [").Append(level).Append("] ")
              .Append(_shortCategory).Append(": ")
              .Append(formatter(state, exception));
            if (exception is not null)
            {
                sb.AppendLine().Append(exception);
            }

            provider.Enqueue(sb.ToString());
        }
    }
}
