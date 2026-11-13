using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HdcMirror.App.Services;
using HdcMirror.Core.Hdc;
using HdcMirror.Core.Scrcpy;

namespace HdcMirror.SelfTest;

/// <summary>
/// 离线自检：只验证「不需要手机、纯函数可判」的逻辑（SPS 解析、内存格式化、分辨率解析、
/// 更新检查的 JSON 解析、设备清单模型的显示字段）。
/// 光跑编译不算验证，这里给出可重复的断言结果。
/// </summary>
internal static class Program
{
    private static int _failed;

    private static int Main()
    {
        Run("SPS 解析（真机采样 32 字节 extradata）", TestSpsFromSample);
        Run("SPS 解析（Annex B 里找 SPS）", TestSpsFromAnnexB);
        Run("IDR 判定", TestContainsIdr);
        Run("内存格式化", TestParseMemory);
        Run("分辨率解析", TestParseResolution);
        Run("Release JSON 解析：有新版本", TestReleaseAvailable);
        Run("Release JSON 解析：已最新", TestReleaseUpToDate);
        Run("Release JSON 解析：异常输入", TestReleaseBadInput);
        Run("设备清单显示字段", TestSavedDeviceDisplay);

        Console.WriteLine();
        if (_failed == 0)
        {
            Console.WriteLine("✔ 全部通过");
            return 0;
        }

        Console.WriteLine($"✗ 失败 {_failed} 项");
        return 1;
    }

    private static void Run(string name, Action test)
    {
        try
        {
            test();
            Console.WriteLine($"  ✔ {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Console.WriteLine($"  ✗ {name}：{ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private static string SamplePath(string fileName)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !Directory.Exists(Path.Combine(directory, "samples")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        if (directory is null)
        {
            throw new InvalidOperationException("找不到 samples 目录（从输出目录向上逐层查找失败）。");
        }

        return Path.Combine(directory, "samples", fileName);
    }

    private static void TestSpsFromSample()
    {
        byte[] extradata = File.ReadAllBytes(SamplePath("extradata.bin"));
        Assert(extradata.Length == 32, $"extradata 应为 32 字节，实际 {extradata.Length}");

        ScrcpySps? sps = ScrcpySpsParser.ParseAnnexB(extradata);
        Assert(sps is not null, "解析结果为空");
        Assert(sps!.Width > 0 && sps.Height > 0, $"宽高异常：{sps.Width}x{sps.Height}");
        Assert(sps.Width == 1260 || sps.Height == 1260, $"预期带 1260 的边，实际 {sps.Width}x{sps.Height}");
        Assert(sps.Width == 2720 || sps.Height == 2720, $"预期带 2720 的边，实际 {sps.Width}x{sps.Height}");
    }

    private static void TestSpsFromAnnexB()
    {
        // 真机采样里的 stream.h264 只有 IDR 片，SPS/PPS 是首条配置帧单独下发的，
        // 所以这里断言「返回 null 且不抛」，再补一个手工拼的 SPS 验证能从 Annex B 里挑出来。
        byte[] stream = File.ReadAllBytes(SamplePath("stream.h264"));
        ScrcpySps? none = ScrcpySpsParser.ParseAnnexB(stream);
        Assert(none is null, "不含 SPS 的码流不应解析出参数集");

        byte[] extradata = File.ReadAllBytes(SamplePath("extradata.bin"));
        byte[] host = new byte[] { 0x00, 0x00, 0x00, 0x01 };
        byte[] withSps = host.Concat(extradata).Concat(host).Concat(new byte[] { 0x65, 0x88 }).ToArray();
        ScrcpySps? sps = ScrcpySpsParser.ParseAnnexB(withSps);
        Assert(sps is not null, "在拼接的 Annex B 里没找到 SPS");
        Assert(sps!.Width > 0, "宽度无效");
    }

    private static void TestContainsIdr()
    {
        byte[] stream = File.ReadAllBytes(SamplePath("stream.h264"));
        Assert(ScrcpySpsParser.ContainsIdrSlice(stream), "真实码流里应含 IDR 片");
        Assert(!ScrcpySpsParser.ContainsIdrSlice(new byte[] { 0, 0, 0, 1, 0x41, 0x00 }), "非 IDR 数据被误判");
    }

    private static void TestParseMemory()
    {
        const string meminfo =
            "MemTotal:       11869524 kB\r\n" +
            "MemFree:          197928 kB\r\n" +
            "MemAvailable:    1813504 kB\r\n" +
            "Buffers:               0 kB\r\n" +
            "Cached:          2798156 kB\r\n" +
            "SwapCached:        12345 kB\r\n";

        string text = HdcDeviceInfoReader.ParseMemory(meminfo);
        Assert(text.Contains("GiB", StringComparison.Ordinal), $"缺少单位：{text}");
        Assert(text.Contains("已用", StringComparison.Ordinal), $"缺少已用：{text}");
        Assert(text.StartsWith("11.", StringComparison.Ordinal), $"总容量不符预期：{text}");
        Assert(text.Contains("9.59", StringComparison.Ordinal), $"已用容量不符预期：{text}");

        string noAvailable = HdcDeviceInfoReader.ParseMemory("MemTotal: 8388608 kB\r\nMemFree: 1048576 kB\r\n");
        Assert(noAvailable.Contains("8.00 GiB", StringComparison.Ordinal), $"缺 MemAvailable 时退化失败：{noAvailable}");

        string empty = HdcDeviceInfoReader.ParseMemory(string.Empty);
        Assert(empty == "—" || empty.Length == 0, $"空输入应有兜底：{empty}");
    }

    private static void TestParseResolution()
    {
        const string dump =
            "OHOS_DUMP_RESOLUTION\n" +
            "render resolution=1260x2720\n" +
            "physical resolution=1260x2720\n";
        string resolution = HdcDeviceInfoReader.ParseResolution(dump);
        Assert(resolution == "1260x2720", $"分辨率解析结果异常：{resolution}");
    }

    private static void TestReleaseAvailable()
    {
        const string json = """
        {
          "tag_name": "v99.0.0",
          "assets": [
            { "name": "HdcMirror-win-Portable.zip", "browser_download_url": "https://example.invalid/portable.zip" },
            { "name": "HdcMirror-win-Setup.exe",   "browser_download_url": "https://example.invalid/setup.exe" }
          ]
        }
        """;

        UpdateCheckResult result = UpdateService.ParseRelease(json);
        Assert(result.Status == UpdateCheckStatus.Available, $"状态应为 Available，实际 {result.Status}");
        Assert(result.Version == "99.0.0", $"版本应为 99.0.0，实际 {result.Version}");
        Assert(result.DownloadUrl is not null, "没挑出下载地址");
        Assert(result.DownloadUrl!.EndsWith("setup.exe", StringComparison.Ordinal), $"应优先挑 Setup.exe，实际 {result.DownloadUrl}");
    }

    private static void TestReleaseUpToDate()
    {
        string sameVersion = string.Format(
            CultureInfo.InvariantCulture,
            "{{ \"tag_name\": \"v{0}\", \"assets\": [] }}",
            UpdateService.Format(UpdateService.CurrentVersion));

        UpdateCheckResult result = UpdateService.ParseRelease(sameVersion);
        Assert(result.Status == UpdateCheckStatus.UpToDate, $"同版本应判 UpToDate，实际 {result.Status}");
    }

    private static void TestReleaseBadInput()
    {
        Assert(
            UpdateService.ParseRelease("{ not json").Status == UpdateCheckStatus.Failed,
            "坏 JSON 应判 Failed");
        Assert(
            UpdateService.ParseRelease("{ \"assets\": [] }").Status == UpdateCheckStatus.Failed,
            "缺 tag_name 应判 Failed");
        Assert(
            UpdateService.ParseRelease("{ \"tag_name\": \"nightly\" }").Status == UpdateCheckStatus.Failed,
            "无法解析的版本号应判 Failed");
    }

    private static void TestSavedDeviceDisplay()
    {
        var device = new SavedDevice { Name = "My Phone", Endpoint = "192.0.2.10:5555" };
        Assert(device.Title == "My Phone", $"标题应为名称，实际 {device.Title}");
        Assert(device.Subtitle == "192.0.2.10:5555", $"副标题应为端点，实际 {device.Subtitle}");
        Assert(device.ToString().Contains("My Phone", StringComparison.Ordinal), "ToString 应含显示名");

        var unnamed = new SavedDevice { Name = string.Empty, Endpoint = "192.0.2.10:5555" };
        Assert(unnamed.Title == "192.0.2.10:5555", $"无名时标题应回退端点，实际 {unnamed.Title}");

        var many = new List<SavedDevice> { unnamed, device };
        Assert(many.Select(static d => d.Endpoint).Distinct().Count() == 1, "同一端点应可去重");
    }
}
