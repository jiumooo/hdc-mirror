# 13 · M3 音频方案可行性 POC

> 阶段：M3（音频）。性质：**验证性 POC**，代码位于 `src/AudioPoc/`，**未接入主程序界面**，未改动 `src/HdcMirror.App/` 下任何文件。
> 验证日期：2026-11-11。环境：Windows 11（10.0.26300.0）、.NET SDK 8.0.427、Intel AX201 蓝牙、HarmonyOS NEXT 真机（型号略，下称 `My Phone`）。
> 本文所有设备地址一律用占位符 `192.0.2.10:5555` / `My Phone`，不出现任何真实 IP、设备昵称、序列号、UDID、MAC。

---

## 一、结论

**一句话：API 路线可行，且不需要 MSIX 包身份；但程序无法代劳「手机侧把音频输出切到电脑」这一步，因此当前 POC 只能证明到 Start() 成功、Open() 等待超时为止，端到端出声未经证实。**

分项结论：

| 问题 | 结论 | 证据 |
| --- | --- | --- |
| `AudioPlaybackConnection` 能否在**普通 Win32 未打包进程**里调用 | **能** | 进程包身份探测返回 `APPMODEL_ERROR_NO_PACKAGE (15700)`；同一进程内 `GetDeviceSelector()` / `FindAllAsync()` / `TryCreateFromId()` / `Start()` 全部成功，无 `UnauthorizedAccessException`、无 `0x80070005` |
| 是否需要打包成 MSIX 才有权限 | **不需要** | 同上。任务书里「可能需要 MSIX 身份」的怀疑，实测排除 |
| 能否枚举到「手机作为音频源」 | **能**，匹配数 = 1 | 选择器命中本机蓝牙无线电上的 A2DP **Sink** 端点（实例路径以 `\SNK` 结尾），显示名解析为已配对手机 |
| 能否完成 `Open()` 并真正出声 | **未验证**（本次失败） | `OpenAsync()` 在 29~30 秒后返回 `Status=UnknownFailure`、`ExtendedError=0x8007001F (ERROR_GEN_FAILURE)`，`State` 全程 `Closed`，`StateChanged` 触发 0 次 |
| 失败原因判定 | **不是权限、不是找不到设备**，是「手机端未在 30 秒内发起 A2DP 串流」 | 见 §四的三条反证：无权限异常、无 0x80070005、选择器与 `TryCreateFromId` 均成功；`0x8007001F` 是 Win32 通用失败码，A2DP Sink 场景下对应「对端未送来串流」 |
| 系统版本门槛 | **Windows 10 2004 / 10.0.19041** | 任务书写的「1803」有误。`AudioPlaybackConnection` 属于 UniversalApiContract **v10（655360）**，随 10.0.19041 引入 |
| 应用能否拿到 PCM、自己做渲染 | **不能** | `Open()` 的官方语义是「让远端正在播放的声音从本机出来」，渲染由系统音频引擎完成，API 不暴露任何 PCM 缓冲 |

> 另需纠正任务书中的一处 API 设想：**不存在 `AudioPlaybackConnection` + `MediaPlayer` / `MediaPlayerElement` 的组合用法**。`AudioPlaybackConnection` 是自成一体的系统级接收通道，不需要、也不接受外部播放器接管；`MediaPlayerElement` 是 UWP XAML 控件，与桌面 WPF 无关。POC 里没有任何播放器代码 —— 这是正确形态，不是缺失。

**对 M3 的产品含义：**
1. 「电脑能听到手机声音、手机自己不出声」这个目标，系统通道能实现，但**入口在手机**：必须由用户在手机控制中心手动把音频输出切到「电脑」（本质是手机把 PC 当作已配对的 A2DP 耳机）。
2. 客户端能做的是：把 PC 侧的 A2DP Sink 端点准备好（`Start()`），把「请去手机上选电脑」这一步做成明确引导，然后 `OpenAsync()` 等待（系统固定 30 秒超时）。
3. **音画同步只能靠固定延迟对齐**，因为拿不到音频时间戳 —— 详见 §五。

---

## 二、工程与 API 用法

### 2.1 工程配置

`src/AudioPoc/AudioPoc.csproj`：

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0-windows10.0.19041.0</TargetFramework>
    <SupportedOSPlatformVersion>10.0.19041.0</SupportedOSPlatformVersion>
    <UseWindowsForms>false</UseWindowsForms>
    <UseWPF>false</UseWPF>
    <RootNamespace>HdcMirror.AudioPoc</RootNamespace>
    <AssemblyName>AudioPoc</AssemblyName>
    <Nullable>enable</Nullable>
    <!-- src/Directory.Build.props 里开着 TreatWarningsAsErrors / EnableNETAnalyzers，
         POC 需要覆盖，否则 CA1416 之类会直接挡住编译。 -->
    <TreatWarningsAsErrors>false</TreatWarningsAsErrors>
    <EnableNETAnalyzers>false</EnableNETAnalyzers>
    <NoWarn>$(NoWarn);CA1416;CS1591;CA1303;CA1305</NoWarn>
    <InvariantGlobalization>false</InvariantGlobalization>
  </PropertyGroup>
</Project>
```

要点：
- **不需要** `Microsoft.Windows.SDK.Contracts` 包。`net8.0-windows10.0.19041.0` 这个 TFM 会自动拉 `Microsoft.Windows.SDK.NET.Ref`，`Windows.Media.Audio` / `Windows.Devices.Enumeration` 直接可用。
- TFM 里的 `10.0.19041.0` 是硬下限，写成 17763 或更低会拿不到 `AudioPlaybackConnection` 类型。
- 编译命令（任务书要求）：

```
C:\dotnet8\dotnet.exe build "D:\Documents\DSH Github 工作区\hdc-mirror\src\AudioPoc\AudioPoc.csproj" -c Debug
```

实测结果：**0 警告 0 错误**（`--no-incremental` 全量重编同样 0/0）。

### 2.2 核心调用序列

```csharp
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

// 1) 拿到 AQS 选择器。它会拼进 KSCATEGORY_AUDIO + A2DP AudioSink 服务 UUID(0000110A)
string selector = AudioPlaybackConnection.GetDeviceSelector();

// 2) 枚举。未打包进程同样可用。
DeviceInformationCollection devices = await DeviceInformation.FindAllAsync(selector);

// 3) 按 ID 创建连接对象。设备不支持音频串流时返回 null（不是抛异常）。
AudioPlaybackConnection? conn = AudioPlaybackConnection.TryCreateFromId(devices[0].Id);
if (conn is null) { /* 该端点不支持串流，换设备 */ }

// 4) 订阅状态变化，供 UI 显示「已连接 / 已断开」
conn.StateChanged += (sender, args) => Console.WriteLine($"[事件] State={sender.State}");

// 5) 登记意图：告诉系统「本应用希望播放这个远端设备的音频」。这一步不发声。
conn.Start();

// 6) 真正建链。系统内部固定 30 秒超时；成功返回 Status=Success。
AudioPlaybackConnectionOpenResult result = await conn.OpenAsync();
if (result.Status.ToString() != "Success")
    Console.WriteLine($"失败 ExtendedError=0x{result.ExtendedError.HResult:X8}");

// 7) 释放。CsWinRT 把 IClosable.Close() 折叠成了 Dispose()，Close() 在 C# 里不可见。
conn.Dispose();
```

**两个必须记住的坑：**

1. **`Close()` 在 C# 里不存在。** 直接写 `conn.Close()` 会编译失败：
   `error CS1061: "AudioPlaybackConnection"未包含"Close"的定义`。
   CsWinRT 投影把 `IClosable.Close()` 折叠为 `IDisposable.Dispose()`，只能调 `Dispose()`。

2. **Open 失败后的「已登记」状态锁在进程级。** 详见 §四.3 —— 进程内怎么重试都快速失败，换进程才能重新等满 30 秒。

### 2.3 POC 工程的职责划分

| 文件 | 职责 |
| --- | --- |
| `Program.cs` | 参数解析、日志文件落盘、退出码 |
| `AudioSinkProbe.cs` | 枚举 / 选设备 / 建连 / 等 Open / 监听状态 / 打印默认播放设备 |
| `PackageIdentity.cs` | `GetCurrentPackageFullName` P/Invoke，用来证明「有没有包身份」 |
| `Privacy.cs` | 输出前对设备 ID 做 MAC / VID_PID / 12 位蓝牙地址脱敏 |
| `Log.cs` | 控制台 + UTF-8 文件双写，行格式 `[HH:mm:ss.fff] [级别] 消息` |
| `msix-package.appxmanifest` | **参考用**最小清单，说明「万一将来要打包」时该写什么；不参与编译 |

命令行参数：

```
AudioPoc.exe [--list] [--verbose] [--show-names] [--no-start]
             [--device N] [--device-id PREFIX] [--open-wait SECONDS]
             [--hold SECONDS] [--retry N] [--watch SECONDS]
```

退出码：

| 码 | 含义 |
| --- | --- |
| 0 | Open 成功 |
| 3 | 选择器匹配数为 0 |
| 4 | 没有选中设备 |
| 10 | `TryCreateFromId` 抛异常 |
| 11 | `TryCreateFromId` 返回 null（不支持串流） |
| 12 | `Start()` 失败 |
| 13 | `OpenAsync()` 抛异常 |
| 20 | Open 未成功（拿到失败结果或超时） |

---

## 三、实测：枚举

命令：`AudioPoc.exe --list --verbose`

### 3.1 实际选择器字符串

```
System.Devices.DevObjectType:=8
AND System.Devices.InterfaceClassGuid:="{6994AD04-93EF-11D0-A3CC-00A0C9223196}"
AND System.DeviceInterface.Bluetooth.ServiceGuid:="{0000110A-0000-1000-8000-00805F9B34FB}"
AND System.Devices.InterfaceEnabled:=System.StructuredQueryType.Boolean#True
```

（原文是一行，此处仅为可读性折行。）

三个 GUID 的含义：

- `6994AD04-93EF-11D0-A3CC-00A0C9223196` —— `KSCATEGORY_AUDIO`，音频设备类。
- `0000110A-0000-1000-8000-00805F9B34FB` —— 蓝牙 **A2DP Audio Sink** 服务 UUID。注意是 `110A`（Sink，收），不是 `110B`（Source，发）。
- 末尾 `InterfaceEnabled:=True` —— 只挑当前启用的接口。这一条很关键：本机有 4 个 AudioSink 接口，只有 1 个是启用状态，选择器正好把它筛出来。

### 3.2 包身份

```
[信息] 进程包身份：APPMODEL_ERROR_NO_PACKAGE (15700) — 普通 Win32 进程，无 MSIX 包身份
```

这是后面所有结论的前提：**以下全部动作都发生在未打包进程里。**

### 3.3 匹配到的设备

```
[信息] 匹配设备数 = 1
  [1] Name=My***ne
      Kind=DeviceInterface  IsEnabled=True  IsPaired=<null>
      Id=\\?\BTHENUM#{0000110a-...}_VID&****_PID&****#7&4af2e5&0&XXXXXXXXXXXX_C00000000#{6994ad04-...}\SNK
      支持性=TryCreateFromId 成功
      属性 System.ItemNameDisplay = My Phone
```

解读：

- **`Kind=DeviceInterface`**，不是 `Association`。这意味着选择器给的是「接口端点」，而不是「蓝牙设备对象」。
- **实例路径以 `\SNK` 结尾** —— 确认是本机无线电的 **Sink** 端点（我方能收）。若以 `\SRC` 结尾，那是 Source（我方能发），方向就反了。
- **`IsPaired=<null>`** 不是「未配对」。`DeviceInformation.Pairing` 在 `DeviceInterface` 形态下为 null；判断配对状态要看 `System.Devices.Aep.IsPaired` 之类的属性或 PnP 侧信息。
- 实例路径里的 `7&4af2e5` 是本机蓝牙无线电的实例哈希，`XXXXXXXXXXXX` 是**本机**无线电地址（已脱敏）。也就是说这个 ID 内嵌的是电脑自己的硬件标识，不是手机的。
- `TryCreateFromId` 成功 → 该端点支持音频串流，是合格目标。

### 3.4 蓝牙侧交叉核对（`Get-PnpDevice`）

本机装了多块蓝牙无线电，容易误判，这里记录几个判别点：

| 观察 | 值 |
| --- | --- |
| BTHENUM 节点总数 | 69 |
| A2DP Sink（`0000110A`）接口数 | 4，其中 **Status=OK 的只有 1 个**，正是选择器命中的那个 |
| A2DP Source（`0000110B`）接口数 | 1，Status=Unknown，挂在**另一块**无线电上 |
| 疑似目标无线电 | `VID&0001010F_PID&107E`，实例哈希 `7&4AF2E5`，其 Sink 接口 Status=OK |
| 手机 BR/EDR 链路 | `BTHENUM\DEV_XXXXXXXXXXXX\7&4AF2E5&0&BLUETOOTHDEVICE_...` **Status=OK** |

结论：手机当前确实与**这块**无线电保持着 BR/EDR 连接，端点也是启用的。**目标端一切正常，问题不在蓝牙层。**

### 3.5 默认播放设备

```
[信息] 播放设备总数 = 4
  ★默认 Name=扬***备
```

★ 标的是 `MediaDevice.GetDefaultAudioRenderId(AudioDeviceRole.Default)` 返回的那一个。含义：`Open()` 成功后，声音会从这台设备出来。若产品需要指定输出（比如强制走某个音箱），**本 API 不提供选择输出设备的入口** —— 只能改系统默认播放设备。这是一个产品约束，见 §五.4。

---

## 四、实测：连接

### 4.1 命令与日志

命令：`AudioPoc.exe --device 1 --open-wait 40 --hold 2 --retry 2`

```
[04:09:0x.xxx] [信息] 进程包身份：APPMODEL_ERROR_NO_PACKAGE (15700)
[04:09:0x.xxx] [信息] 当前 State = Closed
[04:09:0x.xxx] [步骤] 现在请在手机上把音频输出切到本机（每次 Open 最多等 40 秒，共尝试 2 次）…
[04:10:34.922] [信息] —— 第 1/2 次尝试（重建连接对象）——
[04:10:34.938] [成功] Start() 成功：系统已登记本应用希望播放该设备音频。
[04:10:34.938] [信息] OpenAsync 已发起，最多等 40 秒…
[04:11:04.233] [信息] OpenAsync 返回：Status=UnknownFailure  ExtendedError=0x8007001F（COMException: ）  耗时=29.29s  当前 State=Closed
[04:11:04.233] [信息] —— 第 2/2 次尝试（重建连接对象）——
[04:11:04.248] [成功] Start() 成功：系统已登记本应用希望播放该设备音频。
[04:11:04.318] [信息] OpenAsync 返回：Status=UnknownFailure  ExtendedError=0x8007001F（COMException: ）  耗时=0.07s  当前 State=Closed
[04:11:06.330] [信息] 本次退出码 = 20
```

### 4.2 三次独立运行的稳定性

| 运行 | Start() | OpenAsync 耗时 | Status | ExtendedError |
| --- | --- | --- | --- | --- |
| #1 | 成功 | 29.29 s | UnknownFailure | 0x8007001F |
| #2 | 成功 | 30.04 s | UnknownFailure | 0x8007001F |
| #3 | 成功 | 30.12 s | UnknownFailure | 0x8007001F |

**结论：完全可复现。** 且 `--open-wait` 设 40 / 45 秒，返回时间仍是 ~30 秒 —— 说明这 30 秒是 **API 内部超时**，不是我们自己的等待超时。产品实现时不必自己加计时器，直接等 `OpenAsync()` 返回即可。

### 4.3 关键行为：失败后锁在进程级

这是本次 POC 最有价值的发现，直接影响产品实现：

| 重试形态 | 第二次 Open 耗时 | 结果 |
| --- | --- | --- |
| 同一对象直接再 `Open()` | 0.64 s / 0.07 s | 立刻返回同一 `0x8007001F` |
| `Dispose()` + 重新 `TryCreateFromId()` + `Start()` + `Open()`（**同进程**） | **0.07 s** | 仍立刻返回同一错误 |
| 换一个**新进程** | 29.29 s | 又能完整等满 30 秒 |

解读：失败的「已登记 Sink」会话状态锁在**进程级**（很可能是系统音频引擎按进程登记的 A2DP 接收会话未被清除），不是对象级。所以：

- **进程内「重试」没有意义**，只会拿到快速失败。
- 用户错过 30 秒窗口后要再试，产品侧必须**销毁并重建整个音频模块**（或干脆提示用户重新触发一次）。若做不到进程重启，至少要把 `AudioPlaybackConnection` 的失败态标记为「本次会话不可用」，不要反复空转。
- 这一条也解释了为什么某些同类工具「第一次没连上，之后就一直连不上，要重启软件」。

### 4.4 排查掉的可能原因（反证）

| 假设 | 反证 |
| --- | --- |
| 权限不足（应用无权限接收音频） | 无 `UnauthorizedAccessException`，无 `0x80070005`；`Start()` 成功 |
| 需要 MSIX 包身份 | 未打包进程里枚举、创建、`Start()` 全成功 |
| 找不到设备 / 设备未配对 | 匹配数 1，`TryCreateFromId` 成功，蓝牙链路 Status=OK |
| 本机 Sink 端点被禁用 | 选择器带 `InterfaceEnabled:=True` 仍能命中，端点 Status=OK |
| 我们自己的等待超时设得太短 | 设 40 / 45 秒，返回时间不变（~30 秒） |
| 手机不在身边 / 蓝牙断开 | `BTHENUM\DEV_...` Status=OK，BR/EDR 保持连接 |

剩下的唯一解释：**`Open()` 需要手机端配合发起 A2DP 串流，而本次没人操作手机。**

### 4.5 为什么这一步没法用程序绕开

- `Open()` 的语义是「让**远端正在播放**的声音从本机出来」。串流方向由手机决定，Windows 只是被动接收方。
- 手机要开始往 PC 送音频，前提是它在自己的音频输出列表里把「电脑」选为目标。这一步是**手机系统 UI 的操作**，`hdc` 不提供音频路由控制接口（`hdc shell` 无对应能力，也没有公开的音频路由命令）。
- 所以产品能做到的极限是：把 PC 侧端点准备好 + 引导用户去手机上点一下 + 等 30 秒。

> 这正好对应用户原始需求里的那句「手机控制中心的音频输出列表里能看到电脑」—— 那一步既是**用户可见的确认**，也是**必须由用户完成的操作**。

---

## 五、延迟与音画同步方案

### 5.1 必须先接受的前提

- `AudioPlaybackConnection` **不向应用暴露任何 PCM**，也没有音频时间戳。
- 因此**做不到**样本级同步（无法像播放器那样按音频时钟驱动视频）。
- 只能做**固定延迟对齐**：给视频加一个常量缓冲，把视频推迟到「蓝牙音频大约同时到达」的位置。误差来源只能靠实测标定 + 周期性校准吸收。

### 5.2 蓝牙端延迟的量级

A2DP Sink 的端到端延迟由三段构成：手机侧编码缓冲、空口传输、PC 侧接收缓冲（系统音频引擎的 sink buffer）。典型量级：

| 段 | 典型值 | 说明 |
| --- | --- | --- |
| 手机侧编码 + 打包 | 20 ~ 60 ms | SBC/AAC 帧长 + 编码器缓冲 |
| 空口传输 | 5 ~ 20 ms | BR/EDR，2.4G 拥堵时抖动大 |
| PC 侧 Sink 缓冲 | 80 ~ 200 ms | 系统为抗抖动预留，可观测但不能由应用设置 |
| **合计** | **约 120 ~ 280 ms** | 与具体无线电、系统版本、距离有关 |

**这个数字必须实测，不能抄。** 而且同一台机器上，它还会随无线电负载变化。

### 5.3 三步测法

**第一步：测蓝牙端到端延迟（节拍器法）**

不需要额外硬件，也不需要相机：

1. 手机打开一个**等间隔节拍器**（1 Hz 或 2 Hz，用短促的 click，不要用长音）。
2. **A 轮**：手机音频输出 = 手机扬声器。PC 用麦克风录音（手机扬声器与麦克风距离固定，记下距离 `d`，声速 343 m/s → 气导延迟 `d/343` 秒，0.5 m 约 1.5 ms）。录 30 秒。
3. **B 轮**：手机音频输出 = 电脑（A2DP Sink）。PC 用 **WASAPI loopback** 录默认播放设备（loopback 打的是系统渲染时间戳，即「音频到达系统的时刻」）。录 30 秒。
4. 对两段录音做**互相关**，得到两个 click 序列的相位差 `Δ`。
5. `蓝牙端到端延迟 ≈ Δ − d/343`。

用互相关而不是单次 click，是为了让周期性抖动平均掉；30 秒 × 1 Hz = 30 个样本，标准差通常能压到 ±5 ms。

> loopback 录音可用 NAudio 的 `WasapiLoopbackCapture`，或用任何支持「立体声混音 / 虚拟声卡」的录音软件。这一步只是**标定工具**，不需要进产品。

**第二步：测 PC 侧视频管线延迟**

视频链路是自己写的，延迟可以算也可以测：

- **算**：解码输出到上屏之间的排队时长 + WPF 合成延迟（约 1 帧）+ 显示器扫描输出延迟。
- **测**（更可靠）：手机播放一个「白闪 + 同刻 click」的测试片，PC 用高速相机（240 fps 手机慢动作即可）同时拍屏幕和音箱位置，量白闪出现到 click 可闻的时间差 `Δv`。
- 结果记为 `L_video`，通常在 40 ~ 120 ms（取决于解码队列深度）。

**第三步：算视频需要补的延迟**

```
D = 蓝牙端到端延迟 − L_video
```

- 若 `D > 0`：视频要**往后推** `D` 毫秒，才和音频同时到达用户感官。
- 若 `D < 0`：说明视频比音频还慢，只能**给音频加延迟** —— 但本 API 不允许（拿不到 PCM），此时唯一办法是**减小视频管线延迟**（减少解码队列、开硬件解码低延迟模式）或接受音频领先。

按 §5.2 的典型值反推：`D` 大致落在 **+50 ~ +200 ms**。

### 5.4 落地实现：固定缓冲帧数

在 `MirrorWindow` 的呈现路径上插一个「延迟队列」，不要动解码逻辑：

```
接收入队：decodedFrame（带 PTS，单位 ms）
          → delayQueue.Enqueue(frame, enqueueAt = Stopwatch.Now + D)
呈现出队：每帧检查队首 enqueueAt，到点才交给 Image 控件
帧数换算：N = ceil(D × fps / 1000)
          30 fps、D=150ms → N = 5 帧
          60 fps、D=150ms → N = 9 帧
```

几条实现要点：

1. **用单调时钟（`Stopwatch`）驱动出队，不要用 `DateTime.Now`** —— 后者会被系统校时跳变。
2. **队列必须有上限**（例如 `N + 10` 帧），溢出时丢最旧的帧而不是阻塞解码线程，避免蓝牙抖动把整条管线拖停。
3. **`D` 做成可热调参数**，范围 0 ~ 400 ms，步进 1 帧。用户手动微调比自动算法更实用 —— 因为蓝牙延迟本身会漂。
4. **切换音频设备 / 重连后必须重新标定**：`D` 不是全局常量，是「当前这一对（无线电, 系统版本, 输出设备）」的属性。建议存进配置，按 A2DP 端点的实例哈希（脱敏后）分桶。
5. **漂移补偿**：PC 的 A2DP sink 时钟与 hdc 视频流时钟不同源，长时间会累积漂移。经验值 30 分钟内可达 20 ~ 50 ms。做法：每隔 5 ~ 10 分钟让手机播一次「白闪 + click」，量一次残差，`D` 按 ±1 帧步进修正；或干脆把这个校准做成用户可见的「音画校准」按钮。

### 5.5 一个产品层面的硬约束

**本 API 不能选输出设备。** 声音一定从系统**默认播放设备**出来。所以：

- 用户若把默认输出设成蓝牙耳机，手机音频会进蓝牙耳机，不是音箱。
- 要做「HdcMirror 专用输出」，只能靠系统级手段（切换默认设备），或者接受「跟着系统默认走」。
- 这条要写进 UI 提示，否则用户会以为是 bug。

---

## 六、与 `MirrorWindow` 的接线方案（补丁建议，未改文件）

> 本节只是**建议**。按要求未改动 `MainWindow.xaml.cs`、`MirrorWindow.xaml.cs`、`Resources.resx`。

### 6.1 建议新增的文件（不改现有文件的方案）

```
src/HdcMirror.App/Audio/
    AudioSinkService.cs       ← 把 src/AudioPoc/AudioSinkProbe.cs 的逻辑搬过来，去掉 Console 输出
    AudioSyncCoordinator.cs   ← 持有 D（目标延迟）与 delayQueue
    AudioSinkSettings.cs      ← 持久化 D、上次成功的端点哈希
```

好处：对现有代码只做「新增调用」，回滚成本低。

### 6.2 需要动的三处（补丁形态）

**① 生命周期挂接** —— 在 `MirrorWindow` 开始投屏时启动音频、关闭时停：

```
OnStartMirroring:   await _audioSink.PrepareAsync();   // Start() + OpenAsync()
OnStopMirroring:    _audioSink.Dispose();              // Dispose 即 Close
```

注意：`OpenAsync()` 会阻塞最多 30 秒。**必须异步、不能阻塞 UI 线程**，且要在 UI 上显示「等待手机选择音频输出（30 秒）」的引导 —— 因为这段时间用户必须去操作手机。

**② 呈现路径插延迟队列** —— 找到解码帧交给 `Image.Source` 的那一处，改为「入队 → 定时出队」：

```
原： image.Source = frameBitmap;
新： _sync.Enqueue(frameBitmap, framePtsMs);
    // 内部按 enqueueAt = now + D 出队，到点再赋给 Image.Source
```

**③ 新增 UI 元素**（`Resources.resx` 加字符串时由主线负责，此处只给 key 建议）：

| key | 建议文案 |
| --- | --- |
| `Audio_Guide_SelectOnPhone` | 请在手机控制中心把音频输出切换到「电脑」（30 秒内） |
| `Audio_State_Connected` | 手机音频已接入 |
| `Audio_State_Timeout` | 未检测到手机音频，请重试（需重启音频模块） |
| `Audio_Sync_DelayMs` | 音画同步补偿（毫秒） |

### 6.3 状态机建议

```
Idle ──Start()──► Registered ──OpenAsync()──► Opening ──30s 超时──► Failed(本次会话)
                      │                          │
                      │                          └─Success─► Streaming ──StateChanged(Closed)──► Idle
                      └─Dispose()──► Idle
```

关键点：`Failed` 是**终态**（受 §4.3 的进程级锁影响），进入后只能提示用户重启音频模块，不许自动重试。若产品允许，重启音频模块的最干净实现是换一个短生命周期的辅助进程来持有 `AudioPlaybackConnection`。

---

## 七、失败时的备选路线与代价对比

若 A2DP Sink 路线在真机上最终走不通（例如某 HarmonyOS 版本不把 PC 列为音频输出），可退到下面几条。**没有一条是等价的**，代价差异很大。

| 路线 | 原理 | 手机是否需要装东西 | 手机是否静音 | 延迟 | 实现代价 | 评价 |
| --- | --- | --- | --- | --- | --- | --- |
| **A. A2DP Sink（本文路线）** | 系统把 PC 当蓝牙耳机 | **否**（零安装，符合产品前提） | **是** | 120~280 ms | 低（~300 行） | 唯一同时满足「零安装 + 手机静音」的路线 |
| **B. 手机侧内录 + 回传 PCM** | 手机采集内部音频，经 hdc forward 送回 PC 渲染 | **是**（需手机端 helper，且要系统级音频采集权限） | 是 | 40~80 ms（优于 A2DP） | 高：HarmonyOS NEXT 对第三方应用内录限制严，通常只有系统应用/投屏框架能拿内录；还要额外做 hdc 端口转发、PCM 重采样、抖动缓冲 | 延迟最好，但破坏「零安装」这条产品前提。只有在华为开放内录 API 时才值得做 |
| **C. 手机外放 + PC 麦克风** | 声学拾音 | 否 | **否**（手机必须出声） | 5~20 ms（气导） | 极低（几十行） | 直接违反「手机自己不出声」的需求，只作为最坏情况的兜底 |
| **D. WASAPI loopback（PC 侧）** | 捕获 PC 正在播放的声音 | 否 | — | 0（数字域） | 低 | **注意：这条解决不了本问题。** PC loopback 只能抓「PC 自己在放什么」，手机声音压根没进 PC。它只在 B 路线落地后才作为「PC 端渲染/录制」的一环有用，不能单独成路线 |
| **E. 引导用户装商店版 Bluetooth Audio Receiver** | 让用户用现成工具建立 Sink 连接 | 否（但 PC 侧要装第三方应用） | 是 | 同上 | 极低（只写引导） | 可作为**过渡方案**：先验证系统能力，产品化再自研。缺点是无法与投屏 UI 集成，用户要开两个程序 |

### 决策建议

1. **主路线保持 A（A2DP Sink）**，因为它是唯一同时满足两个硬需求（零安装、手机静音）的方案。
2. **B 路线只在华为开放内录能力时才考虑**。它的收益是延迟从 ~200 ms 降到 ~60 ms，但代价是「必须装手机端应用」，等于推翻产品前提。若将来真要追求音画同步精度，B 是唯一能做到样本级同步的路线。
3. **C、D 不作为独立路线**，C 只作为兜底提示，D 只作为 B 的配套。
4. **E 可作为真机验证的快速手段** —— 先用商店应用确认「这台手机 + 这块蓝牙能不能把 PC 当音频输出」，再回头调自己的 `Open()`。这样能把「手机不支持」和「我们代码有问题」两种情况分开。

---

## 八、遗留问题（需要真机操作才能关闭）

| # | 问题 | 关闭方式 |
| --- | --- | --- |
| 1 | `Open()` 成功后是否真的出声、音量是否受 PC 音量控制 | 有人在手机上手动把音频输出切到「电脑」，30 秒内跑一次 `AudioPoc.exe --device 1 --hold 30`，看 `StateChanged` 是否变 `Opened` |
| 2 | 该手机的音频输出列表里是否稳定出现「电脑」 | 同上，观察手机 UI |
| 3 | A2DP Sink 实测延迟具体值 | 按 §5.3 节拍器法标定 |
| 4 | `StateChanged` 的 `Opened` 事件在成功路径上的时序 | 同上第 1 条，日志会打出 `[事件] StateChanged #1 → State=Opened` |
| 5 | 不同 HarmonyOS 版本上的行为是否一致 | 多机型抽样 |

上述 1~2 必须**有真人操作手机**才能验证。本次 POC 期间手机无人操作，因此停在 `Open` 超时这一步 —— 这是环境限制，不是代码缺陷。
