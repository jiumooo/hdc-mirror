// =============================================================================
// HdcMirror.Audio —— 独立进程的蓝牙音频接收器
//
// 为什么单独一个进程：
//   1) 主程序是 net8.0-windows（纯 WPF），加 WinRT 投影就要整体换 TFM，风险大；
//   2) AudioPlaybackConnection 的 Open 超时后会有**进程级锁**（同进程再 Open 立刻返回同一错误），
//      放到子进程里就天然获得「重试＝换个进程」的能力。
//
// 与父进程的约定（一行一条，stdout）：
//   READY              进程起来，准备接管音频
//   NO_DEVICE          系统里没有可作音频源的已配对蓝牙设备
//   OPENING            已 Start()，正在等远端把音频切过来（最长 30 秒，API 内部超时）
//   OPENED             成功：远端声音现在从本机默认播放设备出来
//   CLOSED             远端主动断开 / 手机切回了本机扬声器
//   ERROR 0x8007xxxx   失败，附错误码（父进程据此提示用户）
//   EXIT               收到父进程关闭信号（stdin 结束）
//
// 本 API 不向应用暴露任何 PCM：渲染完全由系统音频引擎完成，因此音画同步只能在父进程侧
// 用固定缓冲做粗对齐（见 docs/13-音频方案POC.md 第五节）。
// =============================================================================

using System.Runtime.InteropServices;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace HdcMirror.Audio;

internal static class Program
{
    /// <summary>Open 的等待窗口：给足手机端切换音频输出的操作时间。</summary>
    private static readonly TimeSpan OpenWait = TimeSpan.FromSeconds(32);

    /// <summary>
    /// 进程级持有连接。**不能用 using 包住**：RunAsync 一返回就 Dispose 会把接收端拆掉，
    /// 手机端刚切过来的音频立刻断，表现为「电脑一闪而过就没了」。只留给进程退出时清理。
    /// </summary>
    private static AudioPlaybackConnection? _connection;

    private static int Main()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("READY");

        // 父进程退出时 stdin 会 EOF —— 那就是收工信号，别留下孤儿进程。
        var watchdog = new Thread(() =>
        {
            try
            {
                while (Console.In.ReadLine() is not null)
                {
                    // 父进程只会发 "quit"，读走即可。
                }
            }
            catch (IOException)
            {
                // stdin 被关掉，等同于退出。
            }

            try
            {
                _connection?.Dispose();
            }
            catch (Exception)
            {
                // 退出路径上释放失败无需上报。
            }

            Console.WriteLine("EXIT");
            Environment.Exit(0);
        })
        {
            IsBackground = true,
        };
        watchdog.Start();

        try
        {
            RunAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.WriteLine("ERROR " + Describe(ex));
            return 1;
        }

        // 保持进程存活：连接断开也不退出，父进程要能看到 CLOSED 状态。
        Thread.Sleep(Timeout.Infinite);
        return 0;
    }

    private static async Task RunAsync()
    {
        string selector = AudioPlaybackConnection.GetDeviceSelector();
        DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);
        if (devices.Count == 0)
        {
            Console.WriteLine("NO_DEVICE");
            return;
        }

        // 选择器已经限定「已配对 + A2DP AudioSink + 接口启用」，正常只会有一台。
        foreach (DeviceInformation info in devices)
        {
            AudioPlaybackConnection? candidate = AudioPlaybackConnection.TryCreateFromId(info.Id);
            if (candidate is not null)
            {
                _connection = candidate;
                Console.WriteLine("DEVICE " + info.Name);
                break;
            }
        }

        if (_connection is null)
        {
            Console.WriteLine("NO_DEVICE");
            return;
        }

        {
            AudioPlaybackConnection connection = _connection;
            connection.StateChanged += (_, _) =>
            {
                Console.WriteLine("STATE " + connection.State);
                if (connection.State == AudioPlaybackConnectionState.Closed)
                {
                    Console.WriteLine("CLOSED");
                }
            };

            // StartAsync 必须在 OpenAsync 之前 await 完成（WinRT 文档要求）：它负责把本机登记成
            // 可接收音频的端点。原来用的是同步 Start()，登记动作还没落地就去 Open，手机侧
            // 「投射音频」里就时有时无。参考实现（AkiLink / AudioPlaybackConnector）都是 await StartAsync。
            try
            {
                await connection.StartAsync().AsTask().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.WriteLine("LOG StartAsync 失败（继续尝试 Open）：" + Describe(ex));
            }

            Console.WriteLine("OPENING");

            using var cts = new CancellationTokenSource(OpenWait);
            try
            {
                // OpenAsync 不靠异常报错：失败时它照样返回结果对象，只是 Status 不为 Success。
                // 原来没看 Status 就打印 OPENED，等于把「没连上」当成了「已连接」。
                AudioPlaybackConnectionOpenResult result =
                    await connection.OpenAsync().AsTask(cts.Token).ConfigureAwait(false);

                switch (result.Status)
                {
                    case AudioPlaybackConnectionOpenResultStatus.Success:
                        Console.WriteLine("OPENED");
                        break;
                    case AudioPlaybackConnectionOpenResultStatus.RequestTimedOut:
                        // 手机没响应（多半是没在控制中心点「投射音频」）。
                        Console.WriteLine("ERROR 0x8007001F 请求超时：手机没接受投射");
                        break;
                    case AudioPlaybackConnectionOpenResultStatus.DeniedBySystem:
                        // 永久性拒绝：系统策略或蓝牙栈拒绝，重试也没用，必须去蓝牙设置处理。
                        Console.WriteLine("DENIED 系统拒绝（检查蓝牙配对与驱动）");
                        break;
                    default:
                        Console.WriteLine("ERROR 0x80004005 未知失败：" + result.Status);
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                // API 内部 30 秒到点通常先返回 0x8007001F，这里兜住"我们自己超时"的分支。
                Console.WriteLine("ERROR 0x8007001F 等待手机投射超时");
            }
            catch (Exception ex)
            {
                Console.WriteLine("ERROR " + Describe(ex));
            }
        }
    }

    /// <summary>把 WinRT 异常压成「HResult + 简短消息」，父进程解析第一段即可。</summary>
    /// <param name="ex">捕获到的异常。</param>
    /// <returns>形如 <c>0x8007001F 设备未就绪</c> 的字符串。</returns>
    private static string Describe(Exception ex)
    {
        if (ex is COMException com)
        {
            return "0x" + com.HResult.ToString("X8") + " " + com.Message.ReplaceLineEndings(" ");
        }

        return "0x" + ex.HResult.ToString("X8") + " " + ex.Message.ReplaceLineEndings(" ");
    }
}
