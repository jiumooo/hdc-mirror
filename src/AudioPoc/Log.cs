using System.Text;

namespace HdcMirror.AudioPoc;

/// <summary>
/// 极简中文日志：同时写控制台与文件。
/// 文件落在 src/AudioPoc/logs/ 下（仓库 .gitignore 已忽略 logs/ 与 *.log）。
/// </summary>
internal static class Log
{
    private static readonly object Gate = new();
    private static StreamWriter? _file;

    public static string? FilePath { get; private set; }

    public static void OpenFile(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }

        _file = new StreamWriter(path, append: false, new UTF8Encoding(false)) { AutoFlush = true };
        FilePath = path;
    }

    public static void CloseFile()
    {
        lock (Gate)
        {
            _file?.Flush();
            _file?.Dispose();
            _file = null;
        }
    }

    public static void Step(string message) => Write("步骤", message);

    public static void Info(string message) => Write("信息", message);

    public static void Ok(string message) => Write("成功", message);

    public static void Warn(string message) => Write("警告", message);

    public static void Error(string message) => Write("错误", message);

    public static void Raw(string message)
    {
        lock (Gate)
        {
            Console.WriteLine(message);
            _file?.WriteLine(message);
        }
    }

    private static void Write(string level, string message)
    {
        var line = $"[{DateTime.Now:HH:mm:ss.fff}] [{level}] {message}";
        lock (Gate)
        {
            Console.WriteLine(line);
            _file?.WriteLine(line);
        }
    }
}
