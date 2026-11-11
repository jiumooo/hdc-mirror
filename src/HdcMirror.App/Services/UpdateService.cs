using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Diagnostics;
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

    /// <summary>
    /// 下载专用客户端：安装包约 90 MB，用检查更新那个 15 秒超时会在慢网下必失败，
    /// 这里不设总超时，靠调用方取消 + 单次读超时兜底。
    /// </summary>
    private static readonly HttpClient DownloadClient = CreateDownloadClient();

    private static HttpClient CreateDownloadClient()
    {
        var client = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("HdcMirror", "1.0"));
        return client;
    }

    /// <summary>仓库是否已配置（占位符未替换时视为未配置）。</summary>
    public static bool IsConfigured => !string.Equals(RepoOwner, "OWNER", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// 当前程序版本号。优先读 <see cref="AssemblyInformationalVersionAttribute"/>，
    /// 它由 <c>Directory.Build.props</c> 的 <c>&lt;Version&gt;</c> 直接派生，带 <c>-beta</c> 之类的后缀也能正确截断；
    /// 拿不到才退回程序集版本（后者可能被手写的 &lt;AssemblyVersion&gt; 拖住，别再依赖它）。
    /// </summary>
    public static Version CurrentVersion
    {
        get
        {
            Assembly? assembly = Assembly.GetEntryAssembly();

            string? informational = assembly?
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
                .InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                string text = informational;

                int plus = text.IndexOf('+', StringComparison.Ordinal);
                if (plus >= 0)
                {
                    text = text[..plus];
                }

                int dash = text.IndexOf('-', StringComparison.Ordinal);
                if (dash >= 0)
                {
                    text = text[..dash];
                }

                if (Version.TryParse(text, out Version? parsed) && parsed is not null)
                {
                    return parsed;
                }
            }

            return assembly?.GetName().Version ?? new Version(0, 0, 0);
        }
    }

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

    /// <summary>下载进度快照。</summary>
    /// <param name="Received">已接收字节数（含续传前已存在的部分）。</param>
    /// <param name="Total">总字节数；服务器未给长度时为 0。</param>
    /// <param name="BytesPerSecond">本次会话的平均速度，刚开始时为 0。</param>
    /// <param name="ResumedFrom">续传起点；0 表示全新下载。</param>
    public readonly record struct DownloadProgress(long Received, long Total, double BytesPerSecond, long ResumedFrom)
    {
        /// <summary>完成比例 0~1；总长未知时返回 0。</summary>
        public double Fraction => Total > 0 ? Math.Clamp((double)Received / Total, 0, 1) : 0;
    }

    /// <summary>
    /// 下载安装包并返回落盘路径。支持断点续传：
    /// 未完成的下载留在 <c>&lt;文件&gt;.part</c>，下次调用带 <c>Range</c> 头接着下，
    /// 服务器返回 206 就追加，返回 200（不支持 Range / 文件已变）就从头重下。
    /// </summary>
    /// <param name="downloadUrl">安装包地址。</param>
    /// <param name="progress">进度回调，可能从后台线程触发。</param>
    /// <param name="cancellationToken">取消标记；取消不会删除 .part，供下次续传。</param>
    public static async Task<string> DownloadAsync(
        string downloadUrl,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
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
        string partial = target + ".part";

        long resumedFrom = File.Exists(partial) ? new FileInfo(partial).Length : 0;

        using var request = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        if (resumedFrom > 0)
        {
            request.Headers.Range = new RangeHeaderValue(resumedFrom, null);
        }

        using HttpResponseMessage response = await DownloadClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && resumedFrom > 0)
        {
            // 服务器说这个区间不存在：多半是 .part 已经和远端一样大（上次其实下完了）。
            File.Move(partial, target, overwrite: true);
            progress?.Report(new DownloadProgress(resumedFrom, resumedFrom, 0, resumedFrom));
            return target;
        }

        response.EnsureSuccessStatusCode();

        bool append = resumedFrom > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
        if (!append)
        {
            resumedFrom = 0;
        }

        long total = resumedFrom + (response.Content.Headers.ContentLength ?? 0);
        long received = resumedFrom;

        progress?.Report(new DownloadProgress(received, total, 0, resumedFrom));

        var clock = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            await using Stream source = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            await using (var destination = new FileStream(
                partial,
                append ? FileMode.Append : FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                81920,
                useAsync: true))
            {
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;

                    double seconds = clock.Elapsed.TotalSeconds;
                    double speed = seconds > 0.25 ? (received - resumedFrom) / seconds : 0;
                    progress?.Report(new DownloadProgress(received, total, speed, resumedFrom));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消时保留 .part，下次接着下。
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException)
        {
            // 断网 / 连接中断同样保留 .part，下次续传。
            throw;
        }

        if (total > 0 && received < total)
        {
            throw new IOException(string.Format(
                CultureInfo.InvariantCulture,
                "下载不完整：{0} / {1} 字节",
                received,
                total));
        }

        File.Move(partial, target, overwrite: true);
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
