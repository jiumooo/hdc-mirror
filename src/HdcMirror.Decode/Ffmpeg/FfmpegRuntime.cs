using System.Globalization;
using DynamicallyLoadedBindings = FFmpeg.AutoGen.Bindings.DynamicallyLoaded.DynamicallyLoadedBindings;
using FFmpeg.AutoGen;
using HdcMirror.Core.Diagnostics;

namespace HdcMirror.Decode.Ffmpeg;

/// <summary>
/// libavcodec 的进程级初始化。
/// <para>
/// 输入：可选的 <see cref="ILogSink"/>；输出：无（只做初始化与诊断记录）。
/// 线程模型：进程内只初始化一次，内部用锁串行化；调用点可以来自任意线程，
/// 但 <see cref="FFmpeg.AutoGen.ffmpeg.RootPath"/> 是全局状态，所以必须在打开任何解码器之前完成。
/// </para>
/// <para>
/// 原生库查找顺序（第一个存在者生效）：
/// 1. 环境变量 <c>HDCMIRROR_FFMPEG_PATH</c>；
/// 2. 程序目录下 <c>native\ffmpeg\win64</c>（构建时由本工程复制）；
/// 3. 程序目录下 <c>native\ffmpeg</c>；
/// 4. 全部落空则不设置 RootPath，交给系统 PATH 解析（开发机装了 FFmpeg 时可用）。
/// </para>
/// </summary>
internal static class FfmpegRuntime
{
    /// <summary>日志分类名。</summary>
    public const string Category = "Decode.Ffmpeg";

    /// <summary>覆盖原生库目录的环境变量名。</summary>
    public const string PathEnvironmentVariable = "HDCMIRROR_FFMPEG_PATH";

    private static readonly object Gate = new();
    private static bool _initialized;
    private static string? _rootPath;
    private static string _versionSummary = string.Empty;

    /// <summary>实际生效的原生库目录；<see langword="null"/> 表示走系统 PATH。</summary>
    public static string? RootPath
    {
        get
        {
            lock (Gate)
            {
                return _rootPath;
            }
        }
    }

    /// <summary>形如 <c>avcodec 63.28.100 / avutil 61.20.100 / swscale 10.6.100</c> 的版本串。</summary>
    public static string VersionSummary
    {
        get
        {
            lock (Gate)
            {
                return _versionSummary;
            }
        }
    }

    /// <summary>初始化 FFmpeg 绑定（幂等）。</summary>
    /// <param name="log">可选的日志接收器。</param>
    public static void EnsureInitialized(ILogSink? log = null)
    {
        lock (Gate)
        {
            if (_initialized)
            {
                return;
            }

            var resolved = ResolveRootPath();
            DynamicallyLoadedBindings.Initialize();
            if (resolved is not null)
            {
                ffmpeg.RootPath = resolved;
            }

            _rootPath = resolved;
            _versionSummary = string.Create(
                CultureInfo.InvariantCulture,
                $"avcodec {FormatVersion(ffmpeg.avcodec_version())} / avutil {FormatVersion(ffmpeg.avutil_version())} / swscale {FormatVersion(ffmpeg.swscale_version())}");
            _initialized = true;

            log?.Info(Category, resolved is null
                ? string.Create(CultureInfo.InvariantCulture, $"libavcodec 已就绪（{_versionSummary}，原生库走系统 PATH）")
                : string.Create(CultureInfo.InvariantCulture, $"libavcodec 已就绪（{_versionSummary}，原生库目录 {resolved}）"));
        }
    }

    private static string FormatVersion(uint packed) =>
        string.Create(CultureInfo.InvariantCulture, $"{(packed >> 16) & 0xFF}.{(packed >> 8) & 0xFF}.{packed & 0xFF}");

    private static string? ResolveRootPath()
    {
        var configured = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(configured) && ContainsNativeLibrary(configured))
        {
            return configured;
        }

        var baseDirectory = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDirectory, "native", "ffmpeg", "win64"),
            Path.Combine(baseDirectory, "native", "ffmpeg"),
        };

        foreach (var candidate in candidates)
        {
            if (ContainsNativeLibrary(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static bool ContainsNativeLibrary(string directory)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return false;
            }

            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                if (name.StartsWith("avcodec-", StringComparison.OrdinalIgnoreCase) ||
                    name.Equals("avcodec.dll", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("libavcodec.so", StringComparison.OrdinalIgnoreCase) ||
                    name.StartsWith("libavcodec.", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        return false;
    }
}
