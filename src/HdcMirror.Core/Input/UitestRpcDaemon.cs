using System.Globalization;
using HdcMirror.Core.Diagnostics;
using HdcMirror.Core.Hdc;

namespace HdcMirror.Core.Input;

/// <summary>
/// 保证设备侧的 hypium RPC 服务（TCP 8012，<c>Gestures</c> 连续触摸的唯一入口）处于监听状态。
/// <para>
/// 关键事实（2026-11-13 真机实测）：<c>uitest start-daemon singleness</c> **不带** <c>--extension-name</c>
/// 时启动的是测试进程，它监听 8012，提供 touchDown/touchMove/touchUp；
/// 带 <c>--extension-name</c> 启动的是投屏服务，只提供 <c>@scrcpy_grpc_socket</c>，**不会**监听 8012。
/// 两者可以同时存在，所以本类在投屏 daemon 之外单独保证 RPC 服务在跑。
/// </para>
/// 没有这个服务时，转发 <c>tcp:xxxx → tcp:8012</c> 会把数据送进一个没人监听的端口，
/// 表现是「连接能建立、写入一两帧后被重置」——看起来像设备拒绝手势，其实是服务没起来。
/// </summary>
public static class UitestRpcDaemon
{
    /// <summary>hypium RPC 固定端口。</summary>
    public const int RpcPort = 8012;

    private const string Category = "input";
    private const string Command = "cd /data/local/tmp && uitest start-daemon singleness";

    /// <summary>
    /// 确认 8012 在监听；不在就尝试拉起测试进程。返回 8012 最终是否可用。
    /// </summary>
    public static async Task<bool> EnsureAsync(HdcRunner runner, ILogSink log, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runner);
        ArgumentNullException.ThrowIfNull(log);

        if (await IsListeningAsync(runner, cancellationToken).ConfigureAwait(false))
        {
            return true;
        }

        log.Info(Category, "设备侧 RPC 服务（8012）没在监听，正在启动测试进程");
        try
        {
            var start = await runner.ShellAsync(Command, cancellationToken).ConfigureAwait(false);
            log.Info(Category, $"uitest start-daemon 退出码 {start.ExitCode}");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            log.Warn(Category, $"启动 RPC 服务失败：{ex.Message}");
        }

        for (var i = 0; i < 12; i++)
        {
            await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            if (await IsListeningAsync(runner, cancellationToken).ConfigureAwait(false))
            {
                log.Info(Category, "RPC 服务已就绪：8012 监听中，连续触摸可用");
                return true;
            }
        }

        log.Warn(Category, "RPC 服务没起来，连续触摸不可用（将退回分段下发）");
        return false;
    }

    /// <summary>读 <c>/proc/net/tcp</c> 判断 8012 是否处于 LISTEN。</summary>
    private static async Task<bool> IsListeningAsync(HdcRunner runner, CancellationToken cancellationToken)
    {
        var result = await runner.ShellAsync("cat /proc/net/tcp", cancellationToken).ConfigureAwait(false);
        if (result.ExitCode != 0)
        {
            return false;
        }

        var needle = ":" + RpcPort.ToString("X4", CultureInfo.InvariantCulture);
        foreach (var line in result.StandardOutput.Split('\n'))
        {
            var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 4)
            {
                continue;
            }

            // 第 2 列是 local_address（16 进制 IP:端口），第 4 列 0A = LISTEN。
            if (parts[1].EndsWith(needle, StringComparison.OrdinalIgnoreCase)
                && parts[3].Equals("0A", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
