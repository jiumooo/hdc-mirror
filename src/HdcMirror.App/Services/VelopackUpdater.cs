using System;
using System.IO;
using Velopack;
using Velopack.Sources;

namespace HdcMirror.App.Services;

/// <summary>
/// Velopack 更新通道。只有在通过安装包装好之后才可用：
/// 安装根目录里存在 <c>Update.exe</c> 就说明是 Velopack 布局，
/// 由它负责增量包、断点续传、替换文件与重启，比自己下载 90 MB 的 Setup 稳得多。
/// </summary>
internal static class VelopackUpdater
{
    /// <summary>当前是否运行在 Velopack 安装布局下。</summary>
    public static bool IsInstalled => FindUpdateExe() is not null;

    /// <summary>创建指向本项目 Releases 的更新管理器。</summary>
    /// <returns>更新管理器。</returns>
    public static UpdateManager Create()
    {
        // 开发调试用：把来源指向本地打包目录，就能离线复现整条更新链路。
        string? overrideSource = Environment.GetEnvironmentVariable("HDCMIRROR_UPDATE_SOURCE");
        if (!string.IsNullOrWhiteSpace(overrideSource))
        {
            return overrideSource.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                ? new UpdateManager(new SimpleWebSource(overrideSource))
                : new UpdateManager(overrideSource);
        }

        var source = new GithubSource(
            repoUrl: $"https://github.com/{UpdateService.RepoOwner}/{UpdateService.RepoName}",
            accessToken: null,
            prerelease: false);

        return new UpdateManager(source);
    }

    /// <summary>在安装根目录里找 <c>Update.exe</c>。</summary>
    /// <returns>找到时返回完整路径，否则返回 <see langword="null"/>。</returns>
    private static string? FindUpdateExe()
    {
        try
        {
            string baseDirectory = AppContext.BaseDirectory.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar);

            DirectoryInfo? current = new(baseDirectory);
            DirectoryInfo? root = current.Parent;
            if (root is null)
            {
                return null;
            }

            string candidate = Path.Combine(root.FullName, "Update.exe");
            return File.Exists(candidate) ? candidate : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }
}
