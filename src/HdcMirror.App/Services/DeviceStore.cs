using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace HdcMirror.App.Services;

/// <summary>
/// 已保存设备的本地清单，落在 <c>%LOCALAPPDATA%\HdcMirror\devices.json</c>。
/// 只保存显示名与端点，不保存序列号 / UDID；文件损坏时按空清单处理，不让界面起不来。
/// </summary>
public sealed class DeviceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly string _path;

    /// <summary>默认位置：<c>%LOCALAPPDATA%\HdcMirror\devices.json</c>。</summary>
    public DeviceStore()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "HdcMirror");
        _path = Path.Combine(root, "devices.json");
    }

    /// <summary>清单文件完整路径，便于在「打开日志目录」时定位。</summary>
    public string FilePath => _path;

    /// <summary>读取清单；文件不存在或解析失败时返回空清单。</summary>
    /// <returns>按最近连接时间倒序的设备列表。</returns>
    public IReadOnlyList<SavedDevice> Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return Array.Empty<SavedDevice>();
            }

            string json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return Array.Empty<SavedDevice>();
            }

            List<SavedDevice>? items = JsonSerializer.Deserialize<List<SavedDevice>>(json, SerializerOptions);
            if (items is null)
            {
                return Array.Empty<SavedDevice>();
            }

            return items
                .Where(item => !string.IsNullOrWhiteSpace(item.Endpoint))
                .OrderByDescending(item => item.LastConnected)
                .ToList();
        }
        catch (IOException)
        {
            return Array.Empty<SavedDevice>();
        }
        catch (JsonException)
        {
            return Array.Empty<SavedDevice>();
        }
        catch (UnauthorizedAccessException)
        {
            return Array.Empty<SavedDevice>();
        }
    }

    /// <summary>写入整份清单；目录不存在时自动创建。</summary>
    /// <param name="items">要保存的设备列表。</param>
    public void Save(IEnumerable<SavedDevice> items)
    {
        ArgumentNullException.ThrowIfNull(items);

        string? directory = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        List<SavedDevice> ordered = items
            .Where(item => !string.IsNullOrWhiteSpace(item.Endpoint))
            .OrderByDescending(item => item.LastConnected)
            .ToList();

        File.WriteAllText(_path, JsonSerializer.Serialize(ordered, SerializerOptions));
    }

    /// <summary>新增或刷新一条记录（端点相同视为同一台设备）。</summary>
    /// <param name="endpoint">设备端点。</param>
    /// <param name="name">设备显示名。</param>
    /// <returns>写入后的完整清单。</returns>
    public IReadOnlyList<SavedDevice> Remember(string endpoint, string? name)
    {
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            return Load();
        }

        List<SavedDevice> items = Load().ToList();
        SavedDevice? existing = items.FirstOrDefault(
            item => string.Equals(item.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            items.Add(new SavedDevice
            {
                Endpoint = endpoint.Trim(),
                Name = string.IsNullOrWhiteSpace(name) ? endpoint.Trim() : name!.Trim(),
                LastConnected = DateTimeOffset.Now,
            });
        }
        else
        {
            existing.LastConnected = DateTimeOffset.Now;
            if (!string.IsNullOrWhiteSpace(name))
            {
                existing.Name = name!.Trim();
            }
        }

        Save(items);
        return Load();
    }

    /// <summary>删除一条记录。</summary>
    /// <param name="endpoint">要删除的端点。</param>
    /// <returns>删除后的完整清单。</returns>
    public IReadOnlyList<SavedDevice> Remove(string endpoint)
    {
        List<SavedDevice> items = Load()
            .Where(item => !string.Equals(item.Endpoint, endpoint, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Save(items);
        return Load();
    }
}
