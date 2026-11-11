using System.Globalization;
using System.Text;

namespace HdcMirror.Core.Diagnostics;

/// <summary>
/// 按天滚动的文件日志，落在 <c>%LOCALAPPDATA%\HdcMirror\logs\</c>，默认只保留最近 7 个文件。
/// <para>
/// 设计取舍：日志本身绝不能成为故障源 —— 所有文件操作都被吞掉异常（写不进去就静默丢弃），
/// 且每次写入立即落盘（<c>AutoFlush</c>），崩溃时不会丢最后几行。
/// </para>
/// </summary>
public sealed class FileLogSink : ILogSink, IDisposable
{
    /// <summary>默认保留的文件个数。</summary>
    public const int DefaultRetainedFiles = 7;

    private const string FilePrefix = "hdc-mirror-";
    private const string FileSuffix = ".log";

    private readonly object _gate = new();
    private readonly int _retainedFiles;
    private readonly LogLevel _minLevel;

    private StreamWriter? _writer;
    private DateOnly _writerDate;
    private bool _disposed;

    /// <summary>创建一个文件日志出口。</summary>
    /// <param name="directory">日志目录；<see langword="null"/> 时用 <see cref="DefaultDirectory"/>。</param>
    /// <param name="minLevel">低于该级别的日志直接丢弃。</param>
    /// <param name="retainedFiles">保留的文件个数，超过后删除最旧的。</param>
    public FileLogSink(string? directory = null, LogLevel minLevel = LogLevel.Info, int retainedFiles = DefaultRetainedFiles)
    {
        if (retainedFiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retainedFiles), retainedFiles, "至少保留 1 个日志文件。");
        }

        DirectoryPath = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory!;
        _minLevel = minLevel;
        _retainedFiles = retainedFiles;

        try
        {
            Directory.CreateDirectory(DirectoryPath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 目录建不出来时保持可用但静默：日志不可用不该阻断业务。
        }
    }

    /// <summary>默认日志目录：<c>%LOCALAPPDATA%\HdcMirror\logs</c>。</summary>
    public static string DefaultDirectory { get; } = AppPaths.Logs;

    /// <summary>实际使用的日志目录。</summary>
    public string DirectoryPath { get; }

    /// <inheritdoc />
    public void Write(LogLevel level, string category, string message, Exception? exception = null)
    {
        if (level < _minLevel)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var line = new StringBuilder(192);
        line.Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture));
        line.Append(" [").Append(Tag(level)).Append(']');
        line.Append(" [").Append(category).Append("] ").Append(message);
        if (exception is not null)
        {
            line.Append(Environment.NewLine).Append(exception);
        }

        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                EnsureWriter(DateOnly.FromDateTime(now.LocalDateTime));
                _writer?.WriteLine(line.ToString());
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ObjectDisposedException)
            {
                // 磁盘写满、文件被占用、目录被删：丢掉这条日志，不影响调用方。
                _writer = null;
            }
        }
    }

    /// <summary>关闭当前文件句柄。</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            try
            {
                _writer?.Flush();
                _writer?.Dispose();
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
                // 关闭失败无需上报。
            }

            _writer = null;
        }
    }

    private static string Tag(LogLevel level) => level switch
    {
        LogLevel.Debug => "DBG",
        LogLevel.Info => "INF",
        LogLevel.Warn => "WRN",
        _ => "ERR",
    };

    private static string FileNameFor(DateOnly date)
        => FilePrefix + date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + FileSuffix;

    private void EnsureWriter(DateOnly today)
    {
        if (_writer is not null && _writerDate == today)
        {
            return;
        }

        try
        {
            _writer?.Flush();
            _writer?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // 换文件失败继续尝试新文件。
        }

        _writer = null;
        Directory.CreateDirectory(DirectoryPath);
        var path = Path.Combine(DirectoryPath, FileNameFor(today));
        var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        _writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true,
        };
        _writerDate = today;
        PruneOldFiles();
    }

    private void PruneOldFiles()
    {
        try
        {
            var files = Directory.GetFiles(DirectoryPath, FilePrefix + "*" + FileSuffix);
            if (files.Length <= _retainedFiles)
            {
                return;
            }

            // 文件名里的日期是零填充的 yyyy-MM-dd，字典序即时间序。
            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (var i = 0; i < files.Length - _retainedFiles; i++)
            {
                try
                {
                    File.Delete(files[i]);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // 单个文件删不掉不影响其余文件。
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 枚举失败就跳过清理，下次再试。
        }
    }
}
