using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace HdcMirror.App.Services;

/// <summary>一次更新检查的结果种类。</summary>
public enum UpdateCheckStatus
{
    /// <summary>有更新。</summary>
    Available,

    /// <summary>已经是最新版。</summary>
    UpToDate,

    /// <summary>还没有配置更新源（仓库地址占位未替换）。</summary>
    NotConfigured,

    /// <summary>仓库或 Release 不存在（还没发过版本，或地址写错）。</summary>
    NotFound,

    /// <summary>网络失败、限流、解析失败等，可重试。</summary>
    Failed,
}

/// <summary>更新检查结果。</summary>
/// <param name="Status">结果种类。</param>
/// <param name="Version">远端最新版本号，非 <see cref="UpdateCheckStatus.Available"/> 时为 <see langword="null"/>。</param>
/// <param name="DownloadUrl">安装包下载地址。</param>
/// <param name="Detail">给用户看的补充说明 / 错误信息。</param>
public sealed record UpdateCheckResult(
    UpdateCheckStatus Status,
    string? Version = null,
    string? DownloadUrl = null,
    string? Detail = null);

/// <summary>
/// 软件内更新：查 GitHub Releases 的最新版本，比当前程序集版本号，新版本就提示下载安装。
/// <para>
/// 只用 BCL：<c>HttpClient</c> + <c>System.Text.Json</c>，不引入 Velopack 之类的运行时依赖。
/// 发布时把安装包（<c>Setup.exe</c>）作为 Release 附件上传，本类按文件名挑出来下载即可。
/// </para>
/// <para>
/// 网络策略：未安装 / 未配置 / 404 都静默写日志，只有「真的发现新版本」才打扰用户；
/// 5xx 或超时归到 <see cref="UpdateCheckStatus.Failed"/>，允许用户手动重试。
/// </para>
/// </summary>
public static class UpdateService
{
    /// <summary>仓库所有者；发版前替换成真实账号。</summary>
    public const string RepoOwner = "jiumooo";

    /// <summary>仓库名。</summary>
    public const string RepoName = "hdc-mirror";

    private const string ApiBase = "https://api.github.com";

    private static readonly HttpClient Client = CreateClient();

    /// <summary>仓库是否已配置（占位符未替换时视为未配置）。</summary>
    public static bool IsConfigured => !string.Equals(RepoOwner, "OWNER", StringComparison.OrdinalIgnoreCase);

    /// <summary>当前程序版本号。</summary>
    public static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    /// <summary>Release 页面地址，用于「打不开更新时手动去下载」。</summary>
    public static string ReleasesPageUrl => $"https://github.com/{RepoOwner}/{RepoName}/releases/latest";

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HdcMirror", "1.0"));
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    /// <summary>查询最新版本。</summary>
    /// <param name="cancellationToken">取消标记。</param>
    public static async Task<UpdateCheckResult> CheckAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return new UpdateCheckResult(UpdateCheckStatus.NotConfigured);
        }

        string url = $"{ApiBase}/repos/{RepoOwner}/{RepoName}/releases/latest";
        try
        {
            using HttpResponseMessage response = await Client.GetAsync(url, cancellationToken).ConfigureAwait(false);
            if (response.StatusCode is System.Net.HttpStatusCode.NotFound)
            {
                return new UpdateCheckResult(UpdateCheckStatus.NotFound);
            }

            if (!response.IsSuccessStatusCode)
            {
                return new UpdateCheckResult(
                    UpdateCheckStatus.Failed,
                    Detail: $"HTTP {(int)response.StatusCode}");
            }

            string json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            return ParseRelease(json);
        }
        catch (TaskCanceledException)
        {
            // HttpClient 的超时也走这个异常；调用方要求取消时把令牌抛回去。
            if (cancellationToken.IsCancellationRequested)
            {
                throw;
            }

            return new UpdateCheckResult(UpdateCheckStatus.Failed, Detail: "请求超时");
        }
        catch (HttpRequestException ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Detail: ex.Message);
        }
    }

    /// <summary>解析 GitHub Release JSON。</summary>
    public static UpdateCheckResult ParseRelease(string json)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;

            string? tag = root.TryGetProperty("tag_name", out JsonElement tagElement)
                ? tagElement.GetString()
                : null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Detail: "Release 缺少 tag_name");
            }

            string versionText = tag.TrimStart('v', 'V');
            if (!Version.TryParse(versionText, out Version? remote))
            {
                return new UpdateCheckResult(UpdateCheckStatus.Failed, Detail: $"无法解析版本号 {tag}");
            }

            string? download = null;
            if (root.TryGetProperty("assets", out JsonElement assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement asset in assets.EnumerateArray())
                {
                    string? name = asset.TryGetProperty("name", out JsonElement nameElement)
                        ? nameElement.GetString()
                        : null;
                    string? href = asset.TryGetProperty("browser_download_url", out JsonElement hrefElement)
                        ? hrefElement.GetString()
                        : null;
                    if (string.IsNullOrWhiteSpace(href))
                    {
                        continue;
                    }

                    if (download is null)
                    {
                        download = href;
                    }

                    if (name is not null
                        && name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                        && name.Contains("Setup", StringComparison.OrdinalIgnoreCase))
                    {
                        download = href;
                        break;
                    }
                }
            }

            if (remote > CurrentVersion)
            {
                return new UpdateCheckResult(UpdateCheckStatus.Available, versionText, download);
            }

            return new UpdateCheckResult(UpdateCheckStatus.UpToDate, versionText);
        }
        catch (JsonException ex)
        {
            return new UpdateCheckResult(UpdateCheckStatus.Failed, Detail: ex.Message);
        }
    }

    /// <summary>
    /// 下载安装包并启动它；调用方通常在返回值之后立刻退出程序。
    /// 下载到 <c>%TEMP%\HdcMirror-update\</c>，文件名取自 URL。
    /// </summary>
    /// <param name="downloadUrl">安装包地址。</param>
    /// <param name="cancellationToken">取消标记。</param>
    public static async Task<string> DownloadAsync(string downloadUrl, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(downloadUrl))
        {
            throw new InvalidOperationException("没有可用的下载地址。");
        }

        string directory = Path.Combine(Path.GetTempPath(), "HdcMirror-update");
        Directory.CreateDirectory(directory);

        string fileName = Path.GetFileName(new Uri(downloadUrl).LocalPath);
        if (string.IsNullOrWhiteSpace(fileName))
        {
            fileName = "HdcMirror-Setup.exe";
        }

        string target = Path.Combine(directory, fileName);
        using HttpResponseMessage response = await Client
            .GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream destination = File.Create(target);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        return target;
    }

    /// <summary>格式化版本号用于界面显示。</summary>
    public static string Format(Version version) => string.Format(
        CultureInfo.InvariantCulture,
        "{0}.{1}.{2}",
        version.Major,
        version.Minor,
        Math.Max(version.Build, 0));
}
