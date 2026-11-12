using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HdcMirror.Core.Diagnostics;

namespace HdcMirror.Core.Hdc;

/// <summary>
/// 读取设备信息九项。
/// <para>
/// 实测结论（详见 docs/12-设备发现与信息读取.md）：单条 <c>hdc shell</c> 往返 1.3~2.0 秒，
/// 九字段若逐条执行需要约 12 秒，因此这里把五条命令**并发**发出（单机实测 5 条并发总耗时 2.3 秒），
/// 再在本地拼装字段；`param get` 不带参数会一次性 dump 全部参数，六个 param 类字段共用这一次输出。
/// </para>
/// <para>
/// 容错：任一条命令失败或超时只让相关字段留空，异常不向上抛；整体取消由调用方的
/// <see cref="CancellationToken"/> 控制。
/// </para>
/// </summary>
public sealed class HdcDeviceInfoReader
{
    private const string Category = "deviceinfo";

    /// <summary>一次性 dump 全部参数（实测 1390 行、1.3 秒）。</summary>
    private const string ParameterDumpCommand = "param get";

    /// <summary>内核版本。实测 <c>cat /proc/version</c> 被 SELinux 拒绝，只有 <c>uname</c> 可用。</summary>
    private const string KernelCommand = "uname -r";

    /// <summary>处理器架构。</summary>
    private const string ArchitectureCommand = "uname -m";

    /// <summary>物理内存。实测 <c>const.product.ram</c> 不存在，只有 <c>/proc/meminfo</c> 可读。</summary>
    private const string MemoryCommand = "cat /proc/meminfo";

    /// <summary>物理分辨率。需带 <c>-a screen</c> 参数，否则 RenderService 无输出。</summary>
    private const string ScreenCommand = "hidumper -s RenderService -a screen";

    /// <summary>设备唯一标识。官方 SN 被 root 门禁挡住，UDID 是普通 shell 能拿到的最接近的标识。</summary>
    private const string UdidCommand = "bm get --udid";

    // ── param 键名 ────────────────────────────────────────────────────────────
    private const string KeyName = "const.product.name";
    private const string KeyMarketName = "const.product.marketname";
    private const string KeyBrand = "const.product.brand";
    private const string KeyModel = "const.product.model";
    private const string KeySoftwareVersion = "const.product.software.version";
    private const string KeyOhosFullName = "const.ohos.fullname";
    private const string KeyDistName = "const.product.os.dist.name";
    private const string KeyDistVersion = "const.product.os.dist.version";
    private const string KeyCpuAbilist = "const.product.cpu.abilist";
    private const string KeyHardwareVersion = "const.product.hardwareversion";

    /// <summary>SN 候选键，按"普通 shell 越可能读到"的顺序排列（实测本机全部被权限拒绝）。</summary>
    private static readonly string[] SerialKeys =
    {
        "const.product.sn",
        "ohos.boot.sn",
        "const.product.serial",
        "ohos.boot.serialno",
        "persist.sys.device.sn",
        "sys.serialno",
    };

    /// <summary>
    /// 型号 → 芯片型号的本地映射表。鸿蒙不向普通 shell 暴露 SoC（相关 param 全部 errNum 106），
    /// 只能按型号查表；表里没有的型号只显示指令集架构，绝不猜测。
    /// </summary>
    private static readonly Dictionary<string, string> SocByModel = new(StringComparer.OrdinalIgnoreCase)
    {
        // 型号是公开的机型代号，不是个人信息；隐私扫描规则会误判，已在 tools\privacy-allowlist.txt 放行。
        ["ALN-AL00"] = "kirin9000s",
        ["ALN-AL10"] = "kirin9000s",
        ["ALN-AL80"] = "kirin9000s",
        ["BRA-AL00"] = "kirin9000s",
        ["MNA-AL00"] = "kirin9010",
        ["ADY-AL00"] = "kirin9010",
        ["HBN-AL00"] = "kirin9010",
    };

    /// <summary>命令失败时即使退出码为 0 也会打印这些字样，需按内容判定而不是只看退出码。</summary>
    private static readonly string[] ErrorMarkers =
    {
        "Permission denied",
        "fail!",
        "inaccessible or not found",
        "No such file or directory",
        "not found",
        "no closing quote",
    };

    /// <summary>UDID 输出形如 <c>udid of current device is :\n6E7C...</c>，取其中十六进制段。</summary>
    private static readonly Regex UdidPattern = new(
        @"(?<udid>[0-9A-Fa-f]{16,64})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PhysicalResolutionPattern = new(
        @"physical resolution=(?<w>\d{3,5})x(?<h>\d{3,5})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex RenderResolutionPattern = new(
        @"render resolution=(?<w>\d{3,5})x(?<h>\d{3,5})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>支持的刷新率：<c>supportedMode[0]: 1260x2720, refreshRate=120</c>。</summary>
    private static readonly Regex SupportedRatePattern = new(
        @"supportedMode\[\d+\]:\s*\d+x\d+,\s*refreshRate=(?<r>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>当前生效的刷新率：<c>activeMode: 1260x2720, refreshRate=60</c>。</summary>
    private static readonly Regex ActiveRatePattern = new(
        @"activeMode:\s*\d+x\d+,\s*refreshRate=(?<r>\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static readonly Regex ActiveModePattern = new(
        @"activeMode:\s*(?<w>\d{3,5})x(?<h>\d{3,5})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MemTotalPattern = new(
        @"MemTotal:\s*(?<kb>\d+)\s*kB",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MemAvailablePattern = new(
        @"MemAvailable:\s*(?<kb>\d+)\s*kB",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex MemFreePattern = new(
        @"MemFree:\s*(?<kb>\d+)\s*kB",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex BuffersPattern = new(
        @"Buffers:\s*(?<kb>\d+)\s*kB",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex CachedPattern = new(
        @"^Cached:\s*(?<kb>\d+)\s*kB",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex ParameterLinePattern = new(
        @"^\s*(?<key>[A-Za-z0-9_.\-]+)\s*=\s*(?<value>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly HdcRunner _runner;
    private readonly ILogSink _log;

    /// <summary>创建读取器。</summary>
    /// <param name="runner">已绑定目标设备的 hdc 执行器。</param>
    /// <param name="log">日志出口，可为空。</param>
    /// <param name="commandTimeout">单条命令超时，默认 3 秒。</param>
    public HdcDeviceInfoReader(HdcRunner runner, ILogSink? log = null, TimeSpan? commandTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(runner);
        _runner = runner;
        _log = log ?? NullLogSink.Instance;
        CommandTimeout = commandTimeout ?? TimeSpan.FromSeconds(3);
        if (CommandTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(commandTimeout), CommandTimeout, "Command timeout must be positive.");
        }
    }

    /// <summary>单条命令超时。</summary>
    public TimeSpan CommandTimeout { get; }

    /// <summary>读取九项设备信息；任一命令失败只影响对应字段。</summary>
    /// <param name="cancellationToken">取消令牌。</param>
    public async Task<HdcDeviceInfo> ReadAsync(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();

        // 六条命令并发：互不依赖，且实测 hdc server 能同时服务多个客户端。
        var parameterTask = TryShellAsync(ParameterDumpCommand, cancellationToken);
        var kernelTask = TryShellAsync(KernelCommand, cancellationToken);
        var architectureTask = TryShellAsync(ArchitectureCommand, cancellationToken);
        var memoryTask = TryShellAsync(MemoryCommand, cancellationToken);
        var screenTask = TryShellAsync(ScreenCommand, cancellationToken);
        var udidTask = TryShellAsync(UdidCommand, cancellationToken);

        var outputs = await Task.WhenAll(parameterTask, kernelTask, architectureTask, memoryTask, screenTask, udidTask)
            .ConfigureAwait(false);

        var parameters = ParseParameters(outputs[0]);
        var kernel = SingleLine(outputs[1]);
        var architecture = SingleLine(outputs[2]);
        var memory = ParseMemory(outputs[3]);
        var resolution = ParseResolution(outputs[4]);
        var displayRates = ParseRefreshRates(outputs[4]);
        var udid = ParseUdid(outputs[5]);

        var missing = new List<string>(4);

        var brand = Parameter(parameters, KeyBrand);
        var model = Parameter(parameters, KeyModel);
        var name = FirstNonEmpty(
            Parameter(parameters, KeyName),
            Parameter(parameters, KeyMarketName),
            string.Join(' ', new[] { brand, model }.Where(v => v.Length > 0)));

        var harmony = FirstNonEmpty(
            Parameter(parameters, KeySoftwareVersion),
            Parameter(parameters, KeyOhosFullName),
            string.Join(' ', new[] { Parameter(parameters, KeyDistName), Parameter(parameters, KeyDistVersion) }
                .Where(v => v.Length > 0)));

        // 型号已经单独占一行，版本串前面重复的型号前缀去掉，屏幕上更好读。
        if (model.Length > 0 && harmony.StartsWith(model, StringComparison.OrdinalIgnoreCase))
        {
            harmony = harmony[model.Length..].TrimStart();
        }

        var cpu = ComposeCpu(architecture, model, Parameter(parameters, KeyCpuAbilist));

        var snValue = Parameter(parameters, SerialKeys);

        // SN 被 root 门禁挡住时，用设备 UDID；仍拿不到才退回 hdc 自己的设备标识（无线是 ip:port，不能当序列号）。
        if (snValue.Length == 0 && udid.Length > 0)
        {
            snValue = udid;
            _log.Debug(Category, "设备未暴露 SN，改用 UDID（bm get --udid）作为序列号。");
        }

        if (snValue.Length == 0)
        {
            snValue = _runner.DeviceSerial ?? string.Empty;
            if (snValue.Length > 0)
            {
                _log.Warn(Category,
                    "设备未暴露 SN 与 UDID，只能退用 hdc 设备标识；无线连接时它不是真实序列号。");
            }
        }

        var hardwareVersion = Parameter(parameters, KeyHardwareVersion);        if (hardwareVersion.Length > 0)
        {
            _log.Debug(Category, $"硬件版本（SoC 型号需 root，仅作旁证）：{hardwareVersion}");
        }

        var info = new HdcDeviceInfo
        {
            Name = name,
            Brand = brand,
            Model = model,
            Serial = snValue,
            HarmonyVersion = harmony,
            KernelVersion = kernel,
            Cpu = cpu,
            Resolution = resolution,
            SupportedRefreshRates = displayRates.Rates,
            ActiveRefreshRate = displayRates.Active,
            Memory = memory,
            MissingFields = BuildMissing(name, brand, model, snValue, harmony, kernel, cpu, resolution, memory),
        };

        stopwatch.Stop();
        _log.Info(Category,
            $"设备信息读取完成：{info}；耗时 {stopwatch.ElapsedMilliseconds} ms，" +
            $"缺 {info.MissingFields.Count} 项{(info.MissingFields.Count > 0 ? "（" + string.Join(",", info.MissingFields) + "）" : string.Empty)}。");
        return info;
    }

    private static IReadOnlyList<string> BuildMissing(
        string name,
        string brand,
        string model,
        string snValue,
        string harmony,
        string kernel,
        string cpu,
        string resolution,
        string memory)
    {
        var missing = new List<string>(4);
        if (name.Length == 0) { missing.Add(nameof(HdcDeviceInfo.Name)); }
        if (brand.Length == 0) { missing.Add(nameof(HdcDeviceInfo.Brand)); }
        if (model.Length == 0) { missing.Add(nameof(HdcDeviceInfo.Model)); }
        if (snValue.Length == 0) { missing.Add(nameof(HdcDeviceInfo.Serial)); }
        if (harmony.Length == 0) { missing.Add(nameof(HdcDeviceInfo.HarmonyVersion)); }
        if (kernel.Length == 0) { missing.Add(nameof(HdcDeviceInfo.KernelVersion)); }
        if (cpu.Length == 0) { missing.Add(nameof(HdcDeviceInfo.Cpu)); }
        if (resolution.Length == 0) { missing.Add(nameof(HdcDeviceInfo.Resolution)); }
        if (memory.Length == 0) { missing.Add(nameof(HdcDeviceInfo.Memory)); }
        return missing;
    }

    /// <summary>执行一条 shell 命令，失败或超时返回 <see langword="null"/>，不抛异常。</summary>
    private async Task<string?> TryShellAsync(string command, CancellationToken cancellationToken)
    {
        using var timeoutSource = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(CommandTimeout);
        try
        {
            var result = await _runner.ShellAsync(command, timeoutSource.Token).ConfigureAwait(false);
            var output = result.CombinedOutput;
            _log.Debug(Category, $"shell [{command}] exit={result.ExitCode} → {Collapse(output, 1500)}");

            if (output.Length == 0)
            {
                _log.Warn(Category, $"命令无输出：{command}");
                return null;
            }

            foreach (var marker in ErrorMarkers)
            {
                if (output.Contains(marker, StringComparison.OrdinalIgnoreCase))
                {
                    _log.Warn(Category, $"命令返回错误内容（{marker}）：{command} → {Collapse(output, 300)}");
                    return null;
                }
            }

            return output;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            _log.Warn(Category, $"命令超时（{CommandTimeout.TotalSeconds:F0} 秒）：{command}");
            return null;
        }
        catch (HdcException ex)
        {
            _log.Warn(Category, $"命令执行失败：{command}", ex);
            return null;
        }
    }

    /// <summary>解析 <c>param get</c> 的全量 dump：每行形如 <c>    key = value</c>，条目之间有空行。</summary>
    /// <param name="dump">命令输出。</param>
    public static IReadOnlyDictionary<string, string> ParseParameters(string? dump)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(dump))
        {
            return map;
        }

        foreach (var rawLine in dump.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            var match = ParameterLinePattern.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var key = match.Groups["key"].Value;
            var value = match.Groups["value"].Value.Trim();
            if (key.Length > 0 && !map.ContainsKey(key))
            {
                map[key] = value;
            }
        }

        return map;
    }

    private static string Parameter(IReadOnlyDictionary<string, string> parameters, string key)
        => parameters.TryGetValue(key, out var value) ? value : string.Empty;

    private static string Parameter(IReadOnlyDictionary<string, string> parameters, IReadOnlyList<string> keys)
    {
        foreach (var key in keys)
        {
            if (parameters.TryGetValue(key, out var value) && value.Length > 0)
            {
                return value;
            }
        }

        return string.Empty;
    }

    private static string ComposeCpu(string architecture, string model, string abilist)
    {
        var arch = abilist.Length > 0 ? $"{architecture} ({abilist})" : architecture;
        if (arch.Length == 0)
        {
            arch = abilist;
        }

        // 型号能查表时，把芯片名放在前面：kirin9000s (arm64-v8a)。
        if (model.Length > 0 && SocByModel.TryGetValue(model.Trim(), out var soc))
        {
            return abilist.Length > 0 ? $"{soc} ({abilist})" : $"{soc} ({architecture})";
        }

        return arch;
    }

    /// <summary>
    /// 从 <c>bm get --udid</c> 的输出里取出 UDID 十六进制串。
    /// 输出是两行（提示语 + UDID），所以不能先压成单行再匹配。
    /// </summary>
    /// <param name="output">命令输出。</param>
    private static string ParseUdid(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        foreach (string line in output.Split('\n'))
        {
            var match = UdidPattern.Match(line);
            if (match.Success && !line.Contains("is :", StringComparison.Ordinal))
            {
                return match.Groups["udid"].Value;
            }
        }

        return string.Empty;
    }

    /// <summary>从 <c>/proc/meminfo</c> 里取 <c>MemTotal</c> 与可用量，换算成 GiB 并保留两位小数。</summary>
    /// <remarks>
    /// 可用量优先取 <c>MemAvailable</c>；部分精简内核没有该字段，退化用
    /// <c>MemFree + Buffers + Cached</c> 估算。界面显示格式为
    /// <c>总容量 GiB (已用 已用量 GiB)</c>，不再附带原始 kB 数字。
    /// </remarks>
    /// <param name="meminfo">命令输出。</param>
    public static string ParseMemory(string? meminfo)
    {
        if (string.IsNullOrWhiteSpace(meminfo))
        {
            return string.Empty;
        }

        var match = MemTotalPattern.Match(meminfo);
        if (!match.Success)
        {
            return string.Empty;
        }

        if (!long.TryParse(match.Groups["kb"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var totalKilobytes)
            || totalKilobytes <= 0)
        {
            return string.Empty;
        }

        var availableKilobytes = ReadKilobytes(MemAvailablePattern, meminfo);
        if (availableKilobytes < 0)
        {
            var free = ReadKilobytes(MemFreePattern, meminfo);
            var buffers = ReadKilobytes(BuffersPattern, meminfo);
            var cached = ReadKilobytes(CachedPattern, meminfo);
            availableKilobytes = Math.Max(0, free) + Math.Max(0, buffers) + Math.Max(0, cached);
            if (availableKilobytes == 0)
            {
                availableKilobytes = -1;
            }
        }

        var totalGibibytes = totalKilobytes / 1048576d;
        if (availableKilobytes < 0 || availableKilobytes >= totalKilobytes)
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"{totalGibibytes:0.00} GiB");
        }

        var usedGibibytes = (totalKilobytes - availableKilobytes) / 1048576d;
        return string.Create(
            CultureInfo.InvariantCulture,
            $"{totalGibibytes:0.00} GiB (已用 {usedGibibytes:0.00} GiB)");
    }

    /// <summary>按给定正则取 kB 数值；取不到返回 -1。</summary>
    private static long ReadKilobytes(Regex pattern, string meminfo)
    {
        var match = pattern.Match(meminfo);
        if (!match.Success)
        {
            return -1;
        }

        return long.TryParse(match.Groups["kb"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var kilobytes)
            ? kilobytes
            : -1;
    }

    /// <summary>从 <c>hidumper -s RenderService -a screen</c> 输出里取物理分辨率。</summary>
    /// <param name="screenDump">命令输出。</param>
    public static string ParseResolution(string? screenDump)
    {
        if (string.IsNullOrWhiteSpace(screenDump))
        {
            return string.Empty;
        }

        foreach (var pattern in new[] { PhysicalResolutionPattern, RenderResolutionPattern, ActiveModePattern })
        {
            var match = pattern.Match(screenDump);
            if (match.Success)
            {
                return match.Groups["w"].Value + "x" + match.Groups["h"].Value;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// 从 <c>hidumper -s RenderService -a screen</c> 输出里取刷新率：
    /// 支持的档位（去重升序）与当前生效档。
    /// </summary>
    /// <param name="screenDump">命令输出。</param>
    /// <returns>读不到时 <c>Rates</c> 为空、<c>Active</c> 为 0。</returns>
    public static (IReadOnlyList<int> Rates, int Active) ParseRefreshRates(string? screenDump)
    {
        if (string.IsNullOrWhiteSpace(screenDump))
        {
            return (Array.Empty<int>(), 0);
        }

        var rates = new SortedSet<int>(Comparer<int>.Default);
        foreach (Match match in SupportedRatePattern.Matches(screenDump))
        {
            if (int.TryParse(match.Groups["r"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rate)
                && rate is >= 24 and <= 240)
            {
                rates.Add(rate);
            }
        }

        int active = 0;
        Match activeMatch = ActiveRatePattern.Match(screenDump);
        if (activeMatch.Success)
        {
            _ = int.TryParse(
                activeMatch.Groups["r"].Value,
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out active);
        }

        return (rates.ToArray(), active);
    }
    private static string SingleLine(string? output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return string.Empty;
        }

        foreach (var line in output.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0)
            {
                return trimmed;
            }
        }

        return string.Empty;
    }

    private static string FirstNonEmpty(params string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate.Trim();
            }
        }

        return string.Empty;
    }

    private static string Collapse(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 16));
        var lastWasSpace = false;
        foreach (var ch in text)
        {
            if (ch is '\r' or '\n' or '\t')
            {
                if (!lastWasSpace && builder.Length > 0)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(ch);
            lastWasSpace = ch == ' ';
            if (builder.Length >= maxLength)
            {
                builder.Append("…(truncated)");
                break;
            }
        }

        return builder.ToString().Trim();
    }
}
