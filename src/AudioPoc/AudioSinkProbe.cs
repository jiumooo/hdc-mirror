using System.Diagnostics;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;
using Windows.Media.Devices;

namespace HdcMirror.AudioPoc;

/// <summary>
/// 对 Windows.Media.Audio.AudioPlaybackConnection 的最小封装，用于验证
/// 「PC 作为 A2DP Sink 接收手机音频」这条系统级路线是否可行。
///
/// 关键语义（已对官方文档核实，见 docs/13-音频方案POC.md）：
///   Start()            —— 向系统登记「本应用希望播放远端设备的音频」，但不发声；
///   Open()/OpenAsync() —— 真正建立连接；成功后远端正在播放的声音会从**本机默认播放设备**出来；
///   StateChanged       —— Closed / Opened 状态变化。
/// 注意：本 API 不向应用暴露任何 PCM，渲染完全由系统音频引擎完成。
/// </summary>
internal sealed class AudioSinkProbe : IDisposable
{
    private AudioPlaybackConnection? _connection;
    private int _stateChangeCount;

    /// <summary>本次使用的 AQS 选择器字符串（原样打印，便于写进报告）。</summary>
    public string SelectorText { get; private set; } = string.Empty;

    /// <summary>枚举可用于创建 AudioPlaybackConnection 的设备，并逐个探测是否支持音频串流。</summary>
    public async Task<int> ListDevicesAsync(bool verbose)
    {
        SelectorText = AudioPlaybackConnection.GetDeviceSelector();
        Log.Info("AQS 选择器 = " + SelectorText);

        DeviceInformationCollection devices;
        try
        {
            devices = await DeviceInformation.FindAllAsync(SelectorText);
        }
        catch (Exception ex)
        {
            Log.Error("FindAllAsync 失败：" + Describe(ex));
            return -1;
        }

        Log.Info("匹配设备数 = " + devices.Count);
        if (devices.Count == 0)
        {
            Log.Warn("选择器为空：说明系统里没有「可作音频源的已配对蓝牙设备」。");
            Log.Warn("排查方向：手机与电脑是否已配对且手机侧音频输出能看到本机。");
            return 0;
        }

        var index = 0;
        foreach (var device in devices)
        {
            index++;
            AudioPlaybackConnection? probe = null;
            string support;
            try
            {
                probe = AudioPlaybackConnection.TryCreateFromId(device.Id);
                support = probe is null
                    ? "TryCreateFromId 返回 null（该设备不支持音频串流）"
                    : "TryCreateFromId 成功";
            }
            catch (Exception ex)
            {
                support = "TryCreateFromId 抛异常：" + Describe(ex);
            }
            finally
            {
                probe?.Dispose();
            }

            Log.Raw($"  [{index}] Name={Privacy.Name(device.Name)}");
            Log.Raw($"      Kind={device.Kind}  IsEnabled={device.IsEnabled}  " +
                    $"IsPaired={(device.Pairing?.IsPaired is null ? "<null>" : device.Pairing.IsPaired.ToString())}");
            Log.Raw($"      Id={Privacy.Id(device.Id)}");
            Log.Raw($"      支持性={support}");

            if (verbose && device.Properties.Count > 0)
            {
                foreach (var kv in device.Properties)
                {
                    Log.Raw("      属性 " + kv.Key + " = " + FormatProperty(kv.Value));
                }
            }
        }

        return devices.Count;
    }

    /// <summary>按 1 起的序号或设备 ID 前缀选一台设备。</summary>
    public async Task<DeviceInformation?> PickDeviceAsync(int index1Based, string? idPrefix)
    {
        var devices = await DeviceInformation.FindAllAsync(
            string.IsNullOrEmpty(SelectorText) ? AudioPlaybackConnection.GetDeviceSelector() : SelectorText);

        if (devices.Count == 0)
        {
            Log.Error("没有可用设备可选中。");
            return null;
        }

        if (!string.IsNullOrEmpty(idPrefix))
        {
            var hit = devices.FirstOrDefault(d => d.Id.Contains(idPrefix, StringComparison.OrdinalIgnoreCase));
            if (hit is null)
            {
                Log.Error("没有设备 ID 包含前缀：" + Privacy.Id(idPrefix));
            }

            return hit;
        }

        if (index1Based < 1 || index1Based > devices.Count)
        {
            Log.Error($"序号 {index1Based} 越界（可用 1..{devices.Count}）。");
            return null;
        }

        return devices[index1Based - 1];
    }

    /// <summary>
    /// 建立连接并尝试 Open。
    /// 返回退出码：0=Open 成功；10=创建抛异常；11=设备不支持串流；12=Start 失败；20=Open 未成功。
    ///
    /// 实测结论（2026-11-11，三次运行一致）：
    ///   Open 失败后，「A2DP Sink 已登记」这个状态会锁在**进程级**——
    ///   ① 在同一个对象上再次 Open：0.07~0.64 秒内立刻返回同一个 0x8007001F；
    ///   ② Dispose 掉对象、重新 TryCreateFromId + Start() 后再次 Open：仍是 0.07 秒返回同一错误；
    ///   ③ 换一个新进程（新对象）：又能完整等满 29~30 秒。
    /// 因此进程内重试没有意义，--retry 只用于验证上述 ②；真正的重试必须换进程。
    /// </summary>
    public async Task<int> ConnectAsync(DeviceInformation device, bool callStart, TimeSpan openWait, TimeSpan hold, int attempts)
    {
        AudioPlaybackConnectionOpenResult? result = null;
        var sw = Stopwatch.StartNew();

        Log.Step($"现在请在手机上把音频输出切到本机（每次 Open 最多等 {openWait.TotalSeconds:0} 秒，共尝试 {attempts} 次）…");

        for (var attempt = 1; attempt <= attempts; attempt++)
        {
            Log.Info($"—— 第 {attempt}/{attempts} 次尝试（重建连接对象）——");

            if (attempt > 1)
            {
                Log.Warn("提醒：进程内重试会快速失败（锁在进程级），这里仅用于验证该行为；真实重试请换进程。");
            }

            // 丢弃旧对象：虽然实测证明进程内重试注定快速失败，但保持对象生命周期干净，
            // 避免把上一次的 StateChanged 订阅与内部状态带进下一次尝试。
            ReleaseConnection();

            Log.Step("尝试创建 AudioPlaybackConnection…");
            try
            {
                _connection = AudioPlaybackConnection.TryCreateFromId(device.Id);
            }
            catch (Exception ex)
            {
                Log.Error("TryCreateFromId 抛异常：" + Describe(ex));
                return 10;
            }

            if (_connection is null)
            {
                Log.Error("TryCreateFromId 返回 null：该设备不支持音频串流，无法继续。");
                return 11;
            }

            _connection.StateChanged += OnStateChanged;
            Log.Ok("连接对象已创建，StateChanged 已订阅。");
            if (attempt == 1)
            {
                Log.Info("连接对象的 DeviceId = " + Privacy.Id(_connection.DeviceId));
            }

            Log.Info("当前 State = " + _connection.State);

            if (callStart)
            {
                try
                {
                    _connection.Start();
                    Log.Ok("Start() 成功：系统已登记本应用希望播放该设备音频。");
                }
                catch (Exception ex)
                {
                    Log.Error("Start() 抛异常：" + Describe(ex));
                    return 12;
                }
            }
            else if (attempt == 1)
            {
                Log.Warn("已按 --no-start 跳过 Start()，用于对比验证 Start 是否必要。");
            }

            var attemptSw = Stopwatch.StartNew();
            try
            {
                var openTask = _connection.OpenAsync().AsTask();
                var finished = await Task.WhenAny(openTask, Task.Delay(openWait)).ConfigureAwait(true);
                attemptSw.Stop();

                if (!ReferenceEquals(finished, openTask))
                {
                    Log.Warn($"OpenAsync 在 {attemptSw.Elapsed.TotalSeconds:0.0} 秒内未返回（手机端尚未选择本机）。");
                    continue;
                }

                result = await openTask.ConfigureAwait(true);
            }
            catch (Exception ex)
            {
                attemptSw.Stop();
                Log.Error($"OpenAsync 抛异常（耗时 {attemptSw.Elapsed.TotalSeconds:0.0} 秒）：" + Describe(ex));
                continue;
            }

            Log.Info($"OpenAsync 返回：Status={result.Status}  ExtendedError={FormatHResult(result.ExtendedError)}" +
                     $"  耗时={attemptSw.Elapsed.TotalSeconds:0.00}s  当前 State={_connection.State}");

            if (result.Status.ToString() == "Success")
            {
                Log.Ok("Open 成功：系统应已开始把手机音频渲染到本机默认播放设备。");
                break;
            }

            Log.Warn("本次 Open 未成功。错误码 0x8007001F(ERROR_GEN_FAILURE) 实测对应「30 秒内手机没有发起 A2DP 串流」。");
        }

        sw.Stop();

        if (result is null)
        {
            Log.Error($"全部 {attempts} 次尝试都没有拿到 Open 结果。");
        }

        Log.Info($"累计 Open 耗时 = {sw.Elapsed.TotalSeconds:0.0}s  当前 State = {_connection?.State.ToString() ?? "<无连接>"}");

        if (hold > TimeSpan.Zero && _connection is not null)
        {
            Log.Step($"保持连接 {hold.TotalSeconds:0} 秒，期间每秒打印一次状态…");
            for (var tick = 1; tick <= (int)hold.TotalSeconds; tick++)
            {
                await Task.Delay(1000).ConfigureAwait(true);
                Log.Info($"  保持中… 已 {tick}s  State={_connection.State}  状态变化次数={_stateChangeCount}");
            }
        }

        var success = result is not null && result.Status.ToString() == "Success";
        return success ? 0 : 20;
    }

    /// <summary>仅监听设备到达/移除，用于确认手机在这一选择器里是否可见。</summary>
    public async Task WatchAsync(TimeSpan duration)
    {
        SelectorText = AudioPlaybackConnection.GetDeviceSelector();
        Log.Info("AQS 选择器 = " + SelectorText);

        var watcher = DeviceInformation.CreateWatcher(SelectorText);
        watcher.Added += (_, d) => Log.Ok($"设备到达：Name={Privacy.Name(d.Name)} Id={Privacy.Id(d.Id)}");
        watcher.Removed += (_, d) => Log.Warn("设备移除：Id=" + Privacy.Id(d.Id));
        watcher.Updated += (_, d) => Log.Info("设备更新：Id=" + Privacy.Id(d.Id));
        watcher.EnumerationCompleted += (_, _) => Log.Ok("初始枚举完成（此后为增量事件）。");
        watcher.Stopped += (_, _) => Log.Info("DeviceWatcher 已停止。");

        try
        {
            watcher.Start();
        }
        catch (Exception ex)
        {
            Log.Error("DeviceWatcher.Start 失败：" + Describe(ex));
            return;
        }

        Log.Info($"开始监听 {duration.TotalSeconds:0} 秒…");
        await Task.Delay(duration).ConfigureAwait(true);
        try
        {
            watcher.Stop();
        }
        catch
        {
            // 停止失败不影响结论，忽略。
        }
    }

    /// <summary>打印系统默认播放设备，说明「声音会从哪出来」。</summary>
    public static async Task ReportDefaultRenderAsync()
    {
        try
        {
            var selector = MediaDevice.GetAudioRenderSelector();
            var renders = await DeviceInformation.FindAllAsync(selector);
            var defaultId = MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default);

            Log.Info("播放设备总数 = " + renders.Count);
            foreach (var render in renders)
            {
                var isDefault = string.Equals(render.Id, defaultId, StringComparison.OrdinalIgnoreCase);
                Log.Raw($"  {(isDefault ? "★默认" : "      ")} Name={Privacy.Name(render.Name)}");
            }

            if (string.IsNullOrEmpty(defaultId))
            {
                Log.Warn("系统未报告默认播放设备 ID。");
            }
        }
        catch (Exception ex)
        {
            Log.Error("枚举播放设备失败：" + Describe(ex));
        }
    }

    private void OnStateChanged(AudioPlaybackConnection sender, object args)
    {
        _stateChangeCount++;
        Log.Ok($"[事件] StateChanged #{_stateChangeCount} → State={sender.State}");
    }

    /// <summary>释放当前连接对象（不打印日志，供重试路径复用）。</summary>
    private void ReleaseConnection()
    {
        if (_connection is null)
        {
            return;
        }

        try
        {
            _connection.StateChanged -= OnStateChanged;

            // CsWinRT 投影把 IClosable.Close() 折叠为 Dispose()，这里统一走 Dispose。
            _connection.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("释放连接时出现异常（可忽略）：" + Describe(ex));
        }
        finally
        {
            _connection = null;
        }
    }

    public void Dispose()
    {
        if (_connection is null)
        {
            return;
        }

        ReleaseConnection();
        Log.Info("连接已释放（Dispose → 底层 Close）。");
    }

    private static string FormatHResult(Exception? error)
    {
        if (error is null)
        {
            return "<null>";
        }

        return $"0x{error.HResult:X8}（{error.GetType().Name}: {error.Message}）";
    }

    private static string FormatProperty(object? value)
    {
        return value switch
        {
            null => "<null>",
            string s => Privacy.Id(s),
            _ => value.ToString() ?? "<null>",
        };
    }

    private static string Describe(Exception ex)
    {
        var text = $"{ex.GetType().FullName} 0x{ex.HResult:X8} {ex.Message}";
        if (ex.InnerException is not null)
        {
            text += " ← 内部异常 " + $"{ex.InnerException.GetType().FullName} 0x{ex.InnerException.HResult:X8} {ex.InnerException.Message}";
        }

        return text;
    }
}
