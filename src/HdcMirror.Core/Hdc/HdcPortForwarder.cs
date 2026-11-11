using System.Globalization;
using System.Net;
using System.Net.Sockets;
using HdcMirror.Core.Diagnostics;

namespace HdcMirror.Core.Hdc;

/// <summary>
/// hdc 端口转发管理：在本地挑一个空闲端口转发到设备侧的 daemon socket。
/// <para>
/// 优先 <c>localabstract:scrcpy_grpc_socket</c>（新路径，daemon 带 extension-name 时才有）；
/// 该路径建不起来时回退到 <c>tcp:5000</c>（老 daemon 只监听本机端口的场景）。
/// </para>
/// <para>
/// 生命周期由本类自己负责：<see cref="Dispose"/> / <see cref="DisposeAsync"/> 一定执行
/// <c>hdc fport rm</c>，异常路径同样会清理（内部 try/finally），避免在设备上留下悬挂转发。
/// </para>
/// </summary>
public sealed class HdcPortForwarder : IDisposable, IAsyncDisposable
{
    private const string Category = "fport";
    private const int FallbackRemotePort = 5000;

    private readonly HdcRunner _runner;
    private readonly ILogSink _log;
    private readonly int _firstPort;
    private readonly int _lastPort;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    /// <summary>创建转发管理器（构造时不接触设备）。</summary>
    /// <param name="runner">hdc 执行器，应已绑定目标设备。</param>
    /// <param name="log">日志出口，可为空。</param>
    /// <param name="firstPort">本地端口扫描起点，默认 15000。</param>
    /// <param name="lastPort">本地端口扫描终点。</param>
    public HdcPortForwarder(HdcRunner runner, ILogSink? log = null, int firstPort = 15000, int lastPort = 15100)
    {
        ArgumentNullException.ThrowIfNull(runner);
        if (firstPort < 1024 || lastPort < firstPort || lastPort > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(firstPort), "端口区间非法。");
        }

        _runner = runner;
        _log = log ?? NullLogSink.Instance;
        _firstPort = firstPort;
        _lastPort = lastPort;
    }

    /// <summary>已建立的本地端口，未建立时为 -1。</summary>
    public int LocalPort { get; private set; } = -1;

    /// <summary>远端端点，形如 <c>localabstract:scrcpy_grpc_socket</c> 或 <c>tcp:5000</c>。</summary>
    public string? RemoteEndpoint { get; private set; }

    /// <summary>是否已建立转发。</summary>
    public bool IsEstablished => LocalPort > 0 && RemoteEndpoint is not null;

    /// <summary>建立转发，返回本地端口。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <exception cref="HdcException">端口区间内没有任何一个建立起转发。</exception>
    public async Task<int> EstablishAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsEstablished)
            {
                return LocalPort;
            }

            var abstractEndpoint = "localabstract:" + ScrcpyDaemon.AbstractSocketName;
            for (var port = _firstPort; port <= _lastPort; port++)
            {
                if (await TryForwardAsync(port, abstractEndpoint, cancellationToken).ConfigureAwait(false))
                {
                    _log.Info(Category, $"已转发 tcp:{LocalPort} → {RemoteEndpoint}");
                    return LocalPort;
                }
            }

            _log.Warn(Category, $"localabstract 路径全部失败，回退 tcp:{FallbackRemotePort}。");
            var fallbackEndpoint = "tcp:" + FallbackRemotePort.ToString(CultureInfo.InvariantCulture);
            for (var port = _firstPort; port <= _lastPort; port++)
            {
                if (await TryForwardAsync(port, fallbackEndpoint, cancellationToken).ConfigureAwait(false))
                {
                    _log.Warn(Category, $"已回退转发 tcp:{LocalPort} → {RemoteEndpoint}");
                    return LocalPort;
                }
            }

            throw new HdcException(
                $"在 {_firstPort}~{_lastPort} 内没有成功建立 fport（localabstract 与 tcp:{FallbackRemotePort} 都失败）。");
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>撤销转发。可重复调用。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task ReleaseAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await ReleaseCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            ReleaseAsync(CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            _log.Error(Category, "撤销 fport 失败，请手动执行 hdc fport ls 检查残留。", ex);
        }
        finally
        {
            _gate.Dispose();
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        try
        {
            await ReleaseAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _log.Error(Category, "撤销 fport 失败，请手动执行 hdc fport ls 检查残留。", ex);
        }
        finally
        {
            _gate.Dispose();
        }
    }

    private async Task ReleaseCoreAsync(CancellationToken cancellationToken)
    {
        if (!IsEstablished)
        {
            return;
        }

        var port = LocalPort;
        var endpoint = RemoteEndpoint!;
        LocalPort = -1;
        RemoteEndpoint = null;

        try
        {
            await RemoveQuietlyAsync(port, endpoint, cancellationToken).ConfigureAwait(false);
            _log.Info(Category, $"已撤销 tcp:{port} → {endpoint}");
        }
        finally
        {
            // 即使 rm 抛异常也已经把状态清空，不会重复撤销。
        }
    }

    private async Task<bool> TryForwardAsync(int port, string remoteEndpoint, CancellationToken cancellationToken)
    {
        if (!IsLocalPortFree(port))
        {
            return false;
        }

        // 同一端口上可能残留上一次的转发，先清再建，否则 fport 会直接失败。
        await RemoveQuietlyAsync(port, remoteEndpoint, cancellationToken).ConfigureAwait(false);

        var add = await _runner
            .RunAsync(new[] { "fport", $"tcp:{port}", remoteEndpoint }, cancellationToken)
            .ConfigureAwait(false);
        if (!add.Succeeded)
        {
            _log.Debug(Category, $"fport tcp:{port} {remoteEndpoint} 失败：{add.CombinedOutput}");
            return false;
        }

        if (!await IsForwardedAsync(port, cancellationToken).ConfigureAwait(false))
        {
            await RemoveQuietlyAsync(port, remoteEndpoint, cancellationToken).ConfigureAwait(false);
            return false;
        }

        LocalPort = port;
        RemoteEndpoint = remoteEndpoint;
        return true;
    }

    private async Task<bool> IsForwardedAsync(int port, CancellationToken cancellationToken)
    {
        var result = await _runner.RunAsync(new[] { "fport", "ls" }, cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            return false;
        }

        // fport ls 每行形如「<设备>  tcp:15000 localabstract:scrcpy_grpc_socket  [Forward]」。
        var needle = "tcp:" + port.ToString(CultureInfo.InvariantCulture) + " ";
        return result.StandardOutput.Contains(needle, StringComparison.Ordinal);
    }

    private async Task RemoveQuietlyAsync(int port, string remoteEndpoint, CancellationToken cancellationToken)
    {
        var result = await _runner
            .RunAsync(
                new[] { "fport", "rm", $"tcp:{port}", remoteEndpoint },
                cancellationToken)
            .ConfigureAwait(false);
        _log.Debug(Category, $"fport rm tcp:{port} {remoteEndpoint} 退出码 {result.ExitCode}");
    }

    private static bool IsLocalPortFree(int port)
    {
        TcpListener? listener = null;
        try
        {
            listener = new TcpListener(IPAddress.Loopback, port);
            listener.Start();
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
        finally
        {
            try
            {
                listener?.Stop();
            }
            catch (SocketException)
            {
                // Stop 失败说明端口状态本来就不干净，交给上层换端口。
            }
        }
    }
}
