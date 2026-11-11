using System.Globalization;
using System.Text;

namespace HdcMirror.AudioPoc;

/// <summary>
/// M3 音频可行性 POC 入口。
///
/// 用法：
///   AudioPoc.exe                      枚举设备 + 尝试连接第 1 台（默认等待 30 秒）
///   AudioPoc.exe --list               仅枚举设备
///   AudioPoc.exe --watch 60           监听 60 秒设备到达/移除
///   AudioPoc.exe --device 2           指定第 2 台设备
///   AudioPoc.exe --device-id Bluetooth#...    指定设备 ID 前缀
///   AudioPoc.exe --no-start           跳过 Start()，对照验证其必要性
///   AudioPoc.exe --open-wait 45 --hold 30     调整等待/保持时长
///   AudioPoc.exe --retry 3             Open 失败时重试 3 次（每次系统内部仍会等 30 秒）
///   AudioPoc.exe --show-names         不脱敏打印设备名（默认脱敏）
/// </summary>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        try
        {
            Console.OutputEncoding = Encoding.UTF8;
        }
        catch
        {
            // 某些宿主不允许改控制台编码，忽略。
        }

        var options = Options.Parse(args);

        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        var logPath = Path.Combine(AppContext.BaseDirectory, "logs", $"audiopoc-{stamp}.log");
        Log.OpenFile(logPath);
        Privacy.ShowNames = options.ShowNames;

        try
        {
            await RunAsync(options).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Error("未捕获异常：" + ex);
            return 99;
        }
        finally
        {
            Log.Info("日志文件：" + logPath);
            Log.CloseFile();
        }

        return 0;
    }

    private static async Task<int> RunAsync(Options options)
    {
        Log.Raw("================================================================");
        Log.Raw(" HdcMirror M3 音频 POC —— PC 作为 A2DP Sink 接收手机音频");
        Log.Raw("================================================================");

        var (hasIdentity, detail) = PackageIdentity.Query();
        Log.Info("包身份检测：" + detail);
        Log.Info("进程是否具备 MSIX 包身份 = " + hasIdentity);
        Log.Info("运行时版本 = " + Environment.Version);
        Log.Info("操作系统版本 = " + Environment.OSVersion.Version);
        Log.Info("进程架构 = " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);

        await AudioSinkProbe.ReportDefaultRenderAsync().ConfigureAwait(false);

        using var probe = new AudioSinkProbe();

        if (options.WatchSeconds > 0)
        {
            await probe.WatchAsync(TimeSpan.FromSeconds(options.WatchSeconds)).ConfigureAwait(false);
            return 0;
        }

        Log.Step("枚举可创建 AudioPlaybackConnection 的设备…");
        var count = await probe.ListDevicesAsync(options.Verbose).ConfigureAwait(false);

        if (options.ListOnly || count <= 0)
        {
            if (count <= 0)
            {
                Log.Info("枚举结果为空，跳过连接尝试。");
            }

            return count <= 0 ? 3 : 0;
        }

        Log.Step($"选择设备（序号 {options.DeviceIndex}）…");
        var device = await probe.PickDeviceAsync(options.DeviceIndex, options.DeviceIdPrefix).ConfigureAwait(false);
        if (device is null)
        {
            Log.Error("未选中任何设备，退出。");
            return 4;
        }

        Log.Ok("已选中：Name=" + Privacy.Name(device.Name));
        Log.Info("           Id=" + Privacy.Id(device.Id));

        var exit = await probe
            .ConnectAsync(
                device,
                callStart: !options.NoStart,
                openWait: TimeSpan.FromSeconds(options.OpenWaitSeconds),
                hold: TimeSpan.FromSeconds(options.HoldSeconds),
                attempts: options.RetryCount)
            .ConfigureAwait(false);

        Log.Step("退出码语义：0=Open 成功；11=设备不支持串流；12=Start 失败；13=Open 抛异常；20=Open 未成功");
        Log.Info("本次退出码 = " + exit);
        return exit;
    }

    private sealed class Options
    {
        public bool ListOnly { get; private set; }

        public bool Verbose { get; private set; }

        public bool ShowNames { get; private set; }

        public bool NoStart { get; private set; }

        public int DeviceIndex { get; private set; } = 1;

        public string? DeviceIdPrefix { get; private set; }

        public int OpenWaitSeconds { get; private set; } = 30;

        public int HoldSeconds { get; private set; } = 15;

        public int RetryCount { get; private set; } = 1;

        public int WatchSeconds { get; private set; }

        public static Options Parse(string[] args)
        {
            var options = new Options();

            for (var i = 0; i < args.Length; i++)
            {
                var arg = args[i];
                switch (arg)
                {
                    case "--list":
                        options.ListOnly = true;
                        break;
                    case "--verbose":
                        options.Verbose = true;
                        break;
                    case "--show-names":
                        options.ShowNames = true;
                        break;
                    case "--no-start":
                        options.NoStart = true;
                        break;
                    case "--device":
                        options.DeviceIndex = ParseInt(Next(args, ref i), options.DeviceIndex);
                        break;
                    case "--device-id":
                        options.DeviceIdPrefix = Next(args, ref i);
                        break;
                    case "--open-wait":
                        options.OpenWaitSeconds = ParseInt(Next(args, ref i), options.OpenWaitSeconds);
                        break;
                    case "--hold":
                        options.HoldSeconds = ParseInt(Next(args, ref i), options.HoldSeconds);
                        break;
                    case "--retry":
                        options.RetryCount = Math.Max(1, ParseInt(Next(args, ref i), options.RetryCount));
                        break;
                    case "--watch":
                        options.WatchSeconds = ParseInt(Next(args, ref i), options.WatchSeconds);
                        break;
                    default:
                        Console.Error.WriteLine("忽略未知参数：" + arg);
                        break;
                }
            }

            return options;
        }

        private static string? Next(string[] args, ref int i)
        {
            if (i + 1 >= args.Length)
            {
                return null;
            }

            i++;
            return args[i];
        }

        private static int ParseInt(string? text, int fallback)
        {
            return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
                ? value
                : fallback;
        }
    }
}
