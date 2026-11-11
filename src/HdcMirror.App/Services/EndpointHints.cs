using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace HdcMirror.App.Services;

/// <summary>本地记住的一条端点线索：只留地址与端口，用来在「忘记设备」之后还能把设备扫回来。</summary>
/// <param name="Endpoint"><c>ip:port</c> 形式。</param>
/// <param name="Name">上一次连接用的地址名，可为空。</param>
/// <param name="LastSeen">上次成功连接或发现的时间。</param>
public sealed record EndpointHint(string Endpoint, string? Name, DateTimeOffset LastSeen);

/// <summary>
/// 端点线索库，落在 <c>%LOCALAPPDATA%\HdcMirror\endpoints.json</c>。
/// 与 <see cref="DeviceStore"/> 刻意分开：设备列表是给用户看的、随时可「忘记」，
/// 而无线调试端口是随机分配的，忘了就再难扫到——线索必须独立保留。
/// </summary>
public sealed class EndpointHints
{
    private const int MaxEntries = 32;
    private static readonly TimeSpan MaxAge = TimeSpan.FromDays(90);
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private readonly object _gate = new();
    private List<EndpointHint>? _cache;

    /// <summary>线索文件路径。</summary>
    public static string FilePath => HdcMirror.Core.AppPaths.EndpointsFile;

    /// <summary>读出仍有效的线索，按最近使用时间倒序。</summary>
    public IReadOnlyList<EndpointHint> List()
    {
        lock (_gate)
        {
            return Load()
                .Where(static h => DateTimeOffset.UtcNow - h.LastSeen < MaxAge)
                .OrderByDescending(static h => h.LastSeen)
                .ToList();
        }
    }

    /// <summary>只取端点字符串，供发现器使用。</summary>
    public IReadOnlyList<string> Endpoints() => List().Select(static h => h.Endpoint).ToList();

    /// <summary>记住一个端点（已存在则刷新时间与名称）。</summary>
    /// <param name="endpoint"><c>ip:port</c>。</param>
    /// <param name="name">设备名，可为空。</param>
    public void Remember(string endpoint, string? name)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return;
        }

        string trimmed = endpoint.Trim();
        lock (_gate)
        {
            var list = Load();
            int existing = list.FindIndex(h => string.Equals(h.Endpoint, trimmed, StringComparison.OrdinalIgnoreCase));
            var hint = new EndpointHint(trimmed, name ?? (existing >= 0 ? list[existing].Name : null), DateTimeOffset.UtcNow);
            if (existing >= 0)
            {
                list[existing] = hint;
            }
            else
            {
                list.Add(hint);
            }

            if (list.Count > MaxEntries)
            {
                list = list.OrderByDescending(static h => h.LastSeen).Take(MaxEntries).ToList();
            }

            Save(list);
        }
    }

    private List<EndpointHint> Load()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        try
        {
            if (!File.Exists(FilePath))
            {
                _cache = new List<EndpointHint>();
                return _cache;
            }

            string json = File.ReadAllText(FilePath);
            _cache = JsonSerializer.Deserialize<List<EndpointHint>>(json) ?? new List<EndpointHint>();
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            _cache = new List<EndpointHint>();
        }

        return _cache;
    }

    private void Save(List<EndpointHint> list)
    {
        try
        {
            string? dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(list, JsonOptions));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // 写不进去就只影响下次扫描能不能找回设备，不影响当前功能。
        }
    }
}
