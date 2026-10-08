using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace PersonalViewer.Diagnostics;

internal static class AppLogger
{
    private const int QueueCapacity = 512;
    private const int BatchSize = 64;
    private const int MaximumMessageLength = 16_384;
    private const int RetainedLogDays = 14;
    private static readonly object FileLock = new();
    private static readonly Encoding LogEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    private static readonly ConcurrentDictionary<string, int> LimitedErrorCounts = new(StringComparer.Ordinal);
    private static readonly Channel<LogEntry> Queue = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(QueueCapacity)
    {
        SingleReader = true,
        SingleWriter = false,
        FullMode = BoundedChannelFullMode.Wait,
        AllowSynchronousContinuations = false
    });
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PersonalViewer",
        "Logs");
    private static Task? _writerTask;
    private static long _droppedLogCount;

    public static void Initialize()
    {
        _writerTask ??= Task.Run(WriteLoopAsync);
        var version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        Info(
            "startup",
            $"version={version}; runtime={RuntimeInformation.FrameworkDescription}; process={Environment.ProcessId}; logDirectory={LogDirectory}");
    }

    public static void Info(string area, string message)
    {
        Write("INFO", area, message);
    }

    public static void Error(string area, Exception exception, string? details = null)
    {
        var message = string.IsNullOrWhiteSpace(details)
            ? exception.ToString()
            : $"{details}{Environment.NewLine}{exception}";
        Write("ERROR", area, message);
    }

    public static void Critical(string area, Exception exception, string? details = null)
    {
        var message = string.IsNullOrWhiteSpace(details)
            ? exception.ToString()
            : $"{details}{Environment.NewLine}{exception}";
        Write("FATAL", area, message, persistImmediately: true);
    }

    public static void ErrorLimited(string area, Exception exception, string? details = null, int limit = 10)
    {
        var count = LimitedErrorCounts.AddOrUpdate(area, 1, static (_, current) => current + 1);
        if (count <= limit)
        {
            Error(area, exception, $"occurrence={count}; {details}");
        }
        else if (count == limit + 1)
        {
            Info(area, $"Further errors in this category are suppressed after {limit} entries.");
        }
    }

    public static void Shutdown()
    {
        Queue.Writer.TryComplete();
        try
        {
            _writerTask?.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // Logging must not prevent the application from closing.
        }
    }

    private static void Write(string level, string area, string message, bool persistImmediately = false)
    {
        if (message.Length > MaximumMessageLength)
        {
            message = $"{message[..MaximumMessageLength]}… [truncated]";
        }

        var droppedCount = Interlocked.Exchange(ref _droppedLogCount, 0);
        if (droppedCount > 0)
        {
            message = $"droppedLogs={droppedCount}; {message}";
        }

        var entry = new LogEntry(DateTimeOffset.Now, Environment.CurrentManagedThreadId, level, area, message);
        if (persistImmediately)
        {
            WriteBatch([entry], flushToDisk: true);
        }
        else if (!Queue.Writer.TryWrite(entry))
        {
            Interlocked.Add(ref _droppedLogCount, droppedCount + 1);
        }
    }

    private static async Task WriteLoopAsync()
    {
        PruneOldLogs();
        var batch = new List<LogEntry>(BatchSize);
        while (await Queue.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            batch.Clear();
            while (batch.Count < BatchSize && Queue.Reader.TryRead(out var entry))
            {
                batch.Add(entry);
            }

            if (batch.Count > 0)
            {
                WriteBatch(batch, flushToDisk: false);
            }
        }
    }

    private static void WriteBatch(IReadOnlyList<LogEntry> entries, bool flushToDisk)
    {
        try
        {
            var batchesByDay = new Dictionary<string, StringBuilder>(StringComparer.Ordinal);
            foreach (var entry in entries)
            {
                var day = entry.Timestamp.ToLocalTime().ToString("yyyyMMdd", CultureInfo.InvariantCulture);
                if (!batchesByDay.TryGetValue(day, out var content))
                {
                    content = new StringBuilder();
                    batchesByDay.Add(day, content);
                }

                content.Append(entry.Timestamp.ToString("O"))
                    .Append("\tthread=").Append(entry.ThreadId)
                    .Append('\t').Append(entry.Level)
                    .Append('\t').Append(entry.Area)
                    .Append('\t').AppendLine(entry.Message);
            }

            lock (FileLock)
            {
                Directory.CreateDirectory(LogDirectory);
                foreach (var (day, content) in batchesByDay)
                {
                    var logPath = Path.Combine(LogDirectory, $"personalviewer-{day}.log");
                    using var stream = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    using var writer = new StreamWriter(stream, LogEncoding, bufferSize: 4096, leaveOpen: true);
                    writer.Write(content);
                    writer.Flush();
                    if (flushToDisk)
                    {
                        stream.Flush(flushToDisk: true);
                    }
                }
            }
        }
        catch (Exception)
        {
            // Diagnostics must never become a new source of application failures.
        }
    }

    private static void PruneOldLogs()
    {
        try
        {
            if (!Directory.Exists(LogDirectory))
            {
                return;
            }

            var cutoff = DateTime.Now.AddDays(-RetainedLogDays);
            foreach (var path in Directory.EnumerateFiles(LogDirectory, "personalviewer-*.log"))
            {
                if (File.GetLastWriteTime(path) < cutoff)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception)
        {
            // Log cleanup is best-effort and must not affect startup.
        }
    }

    private sealed record LogEntry(DateTimeOffset Timestamp, int ThreadId, string Level, string Area, string Message);
}
