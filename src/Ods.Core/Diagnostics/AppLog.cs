using System.Globalization;
using System.Text;

namespace Ods.Core.Diagnostics;

public sealed record LogEntry(DateTimeOffset Time, string Level, string Message)
{
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Time:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level}] {Message}");
}

/// <summary>
/// The desktop app's log: one file per day under the data root's logs folder,
/// readable while ODS runs, plus a bounded in-memory tail for the Logs page.
/// Files older than <see cref="RetentionDays"/> are pruned at startup.
/// </summary>
public static class AppLog
{
    public const int RetentionDays = 14;
    private const int RecentCapacity = 2000;
    private static readonly Lock Gate = new();
    private static readonly Queue<LogEntry> RecentEntries = new();
    private static string? _directory;
    private static string _stem = "ods-desktop";
    private static StreamWriter? _writer;
    private static DateOnly _writerDay;

    public static event Action<LogEntry>? EntryWritten;

    public static string? Directory => _directory;

    public static string? CurrentFile { get; private set; }

    public static void Configure(string directory, string stem = "ods-desktop")
    {
        lock (Gate)
        {
            System.IO.Directory.CreateDirectory(directory);
            _directory = directory;
            _stem = stem;
            _writer?.Dispose();
            _writer = null;
            Prune(directory);
        }
    }

    public static void Info(string message) => Write("INFO", message);

    public static void Warn(string message) => Write("WARN", message);

    public static void Error(string message, Exception? error = null) =>
        Write("ERROR", error is null ? message : $"{message}{Environment.NewLine}{error}");

    public static IReadOnlyList<LogEntry> Recent()
    {
        lock (Gate) return RecentEntries.ToList();
    }

    private static void Write(string level, string message)
    {
        var entry = new LogEntry(DateTimeOffset.Now, level, message);
        lock (Gate)
        {
            RecentEntries.Enqueue(entry);
            while (RecentEntries.Count > RecentCapacity) RecentEntries.Dequeue();
            WriterFor(entry.Time)?.WriteLine(entry.ToString());
        }
        EntryWritten?.Invoke(entry);
    }

    private static StreamWriter? WriterFor(DateTimeOffset time)
    {
        if (_directory is null) return null;
        var day = DateOnly.FromDateTime(time.LocalDateTime);
        if (_writer is not null && day == _writerDay) return _writer;
        _writer?.Dispose();
        CurrentFile = Path.Combine(_directory, string.Create(CultureInfo.InvariantCulture, $"{_stem}-{day:yyyyMMdd}.log"));
        var stream = new FileStream(CurrentFile, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)) { AutoFlush = true };
        _writerDay = day;
        return _writer;
    }

    private static void Prune(string directory)
    {
        var cutoff = DateTime.Now.AddDays(-RetentionDays);
        foreach (var file in new DirectoryInfo(directory).EnumerateFiles("*.log"))
        {
            if (file.LastWriteTime < cutoff) file.Delete();
        }
    }
}
