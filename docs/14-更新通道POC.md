# 14 · 更新通道 POC（M5）

> 阶段：M5「应用内更新」可行性验证
> 日期：2026-11-11
> 结论性质：**打包链路已实跑打通；应用内检查更新的失败路径已取证；成功路径因无真实 Release 未验证。**
> 本文只做验证与建议。**未改动** `src\HdcMirror.App\`、`src\HdcMirror.Core\`、`src\HdcMirror.Decode\`、`src\HdcMirror.sln`、`Resources.resx` 中的任何文件。

---

## 一、结论

**Velopack 在本项目可行，且是「免管理员 + 应用内一键更新」这一组合下最省事的选择。** 打包链路 6 秒出全套产物，`Setup.exe` 89.26 MB、全量包 `nupkg` 82.09 MB。代价与坑集中在四处：

| # | 坑 | 影响 | 处置 |
| --- | --- | --- | --- |
| 1 | `vpk pack` 有**硬门禁**：主程序 `Main` 里没有 `VelopackApp.Build().Run()` 就拒绝打包（FTL） | POC 阶段必须先 `--skipVeloAppCheck` 才能出包 | 正式发版前必须在入口接入，否则装好的包**无法自我更新** |
| 2 | `dist` 既是发布产物目录、又是**增量更新的基线库**：同版本重打会被拒（`equal or greater to the current version`） | 反复调试同一版本时会卡住 | 同版本重打用 `-CleanDist`（会丢增量基线）；跨版本发版**不要**清 |
| 3 | 体积被 FFmpeg 原生库支配：stage 200.29 MB 里约 183.6 MB 是 FFmpeg | 全量更新一次 82 MB | 靠 delta 增量更新压缩；后续可评估裁掉未用的 `avfilter`/`avdevice` |
| 4 | 不做代码签名（既定决策） | 首次运行 SmartScreen「未知发布者」拦截；包内 31 个文件未签名警告 | 发版说明里写清「更多信息 → 仍要运行」，不改技术方案 |

**可行性判定：**

- 打包：✅ 跑通，产物齐全，清单自洽。
- 应用内检查：⚠️ 骨架可用，但**成功路径未验证**（占位仓库无 Release）。失败路径已取证，且失败形态是「普通 HTTP 404」，对正式版的静默策略很友好。
- 免管理员自动更新：✅ 架构上成立（Velopack 装到用户目录），但增量/静默安装**未实测**（需要真实 Release + 上一版安装包）。

---

## 二、环境与版本锁定

| 项 | 值 | 说明 |
| --- | --- | --- |
| .NET SDK | `C:\dotnet8\dotnet.exe` = **8.0.427** | PATH 里的 `dotnet` 是 6.0，**所有命令必须写全路径** |
| Velopack CLI (`vpk`) | **1.2.161** | `%USERPROFILE%\.dotnet\tools\vpk.exe` |
| NuGet `Velopack` 库 | **1.2.161** | 与 CLI **必须同版**，否则清单格式/校验算法可能不兼容 |
| 包 ID | `HdcMirror` | `--packId`，安装目录名与卸载项都用它 |
| 通道 | `win` | `--channel`，决定清单名 `releases.win.json` |
| 运行时 | `win-x64` | 必须显式传，否则 vpk 默认 x86 并告警 |
| 版本号来源 | `src\Directory.Build.props` → `<Version>0.1.0</Version>` | **唯一来源**，脚本读取，不写死 |

> `vpk --version` 不是合法参数（会报错），要看版本用 `vpk --help` 首行。

---

## 三、打包链路

### 3.1 脚本

`tools\pack\build-release.ps1`（PowerShell 5.1 兼容，**UTF-8 带 BOM** 保存——否则 5.1 按 GBK 读中文注释会直接解析失败）。

参数：

| 参数 | 作用 |
| --- | --- |
| `-SelfContained` | 改为自包含发布（体积 +158 MB，见 §5） |
| `-Framework <id>` | 框架依赖模式下让 Setup 自动装运行时（默认不传，见 §5 决策） |
| `-Channel <name>` | 默认 `win` |
| `-SkipPublish` | 复用已有 stage，只重跑 pack（调试脚本时省 40 秒） |
| `-KeepStage` | 不清 stage（默认会清，防旧版本 dll 混进新包） |
| `-SkipVeloAppCheck` | 跳过 `VelopackApp` 静态校验。**仅 POC 用，正式发版必须去掉** |
| `-CleanDist` | 打包前清空 dist。**仅同版本重打时用** |

六步流程：

```
① 读 src\Directory.Build.props 的 <Version>（正则 <Version>\s*([^<]+?)\s*</Version>），
   取不到直接 throw；再校验 ^\d+\.\d+\.\d+ 形态
② 备 stage / dist（默认清 stage；-CleanDist 才清 dist）
③ C:\dotnet8\dotnet.exe publish src\HdcMirror.App\HdcMirror.App.csproj
     -c Release -r win-x64 --self-contained <bool> -o tools\pack\stage
     -p:PublishReadyToRun=false
   校验 stage 内有 HdcMirror.exe 与 tools\hdc.exe
④ vpk 已存在则 dotnet tool update -g vpk --version 1.2.161，否则 install
⑤ vpk pack --packId HdcMirror --packVersion <版本> --packDir tools\pack\stage
     --mainExe HdcMirror.exe --outputDir tools\pack\dist
     --channel win --runtime win-x64 --packTitle HdcMirror --packAuthors HdcMirror
     --delta BestSpeed [--skipVeloAppCheck] [--framework ...]
     --icon src\HdcMirror.App\Assets\app.ico
⑥ 列产物 + 校验 *-Setup.exe 与 releases.*.json 存在，缺一即 exit 1
```

### 3.2 实跑记录（vpk 1.2.161，版本 0.1.0）

```
[04:10:31 INF] Velopack CLI 1.2.161, for distributing applications.
[04:10:31 INF] Beginning to package Velopack release 0.1.0.
[04:10:31 INF] Skipping VelopackApp.Build.Run() check.
[04:10:31 INF] Shortcuts: Desktop,StartMenuRoot
[04:10:32 WRN] No signing parameters provided, 31 file(s) will not be signed.
[04:10:32 INF] Starting: Building portable package
[04:10:32 INF] Starting: Building release 0.1.0
[04:10:37 INF] Complete: Building release 0.1.0
[04:10:37 INF] Setup bundle created 'HdcMirror-win-Setup.exe'.
[04:10:37 WRN] No signing parameters provided, 1 file(s) will not be signed.
[04:10:38 INF] Finished in 00:00:06.5522971.
```

publish 阶段耗时约 40 秒（含 FFmpeg 复制），pack 阶段 6.55 秒。

---

## 四、产物清单与体积

`tools\pack\dist\`（框架依赖，vpk 1.2.161，0.1.0）：

| 文件 | 字节 | 体积 | 作用 | 是否必须上传 GitHub |
| --- | --- | ---: | --- | --- |
| `HdcMirror-win-Setup.exe` | 93,593,572 | 89.26 MB | 新用户下载安装；**也用于老用户回退重装** | ✅ 必须 |
| `HdcMirror-0.1.0-full.nupkg` | 86,077,412 | 82.09 MB | **应用内更新的实体包**，`releases.win.json` 里 `FileName` 指向它 | ✅ **必须**，缺它就升不了级 |
| `HdcMirror-win-Portable.zip` | 86,074,609 | 82.09 MB | 免安装绿色版 | ⭕ 建议（不想发的可不发） |
| `releases.win.json` | 252 | — | 通道清单，`GithubSource` 第一个抓的就是它 | ✅ **必须** |
| `assets.win.json` | 199 | — | 资产索引（Setup/Full/Portable 的文件名与类型） | ✅ 必须 |
| `RELEASES` | 79 | — | 旧式 v1 客户端格式（SHA1 行），为兼容保留 | ✅ 必须（体积可忽略） |
| 合计 | — | **约 253.44 MB** | 单个 Release 的全套附件 | — |

### 4.1 清单原文

`releases.win.json`（注意只有一个 `Full` 资产，没有 delta —— 因为 dist 被打包前清空过，没有基线可比）：

```json
{"Assets":[{"PackageId":"HdcMirror","Version":"0.1.0","Type":"Full","FileName":"HdcMirror-0.1.0-full.nupkg","SHA1":"19D44CBC92129EBD62C56EE2D4A4A1C250E4E78D","SHA256":"9536D7B5C18DC002BC86A4C17ABC01D7A46EABBFCD9332A44BA315C552A66B13","Size":86077412}]}
```

`assets.win.json`：

```json
[{"RelativeFileName":"HdcMirror-win-Setup.exe","Type":"Installer"},{"RelativeFileName":"HdcMirror-0.1.0-full.nupkg","Type":"Full"},{"RelativeFileName":"HdcMirror-win-Portable.zip","Type":"Portable"}]
```

`RELEASES`：

```
19D44CBC92129EBD62C56EE2D4A4A1C250E4E78D HdcMirror-0.1.0-full.nupkg 86077412
```

> `releases.win.json` 里带 **SHA256**，Velopack 下载完会校验；所以 Release 上的 nupkg 必须与打包出来的是同一个字节，不能重新压缩、不能改名。

---

## 五、体积构成与自包含决策

### 5.1 stage 里到底是什么（34 文件 / 200.29 MB）

| 文件 | 体积 |
| --- | ---: |
| `avcodec-63.dll` | 114.94 MB |
| `avfilter-12.dll` | 36.19 MB |
| `avformat-63.dll` | 22.05 MB |
| `Wpf.Ui.dll` | 6.26 MB |
| `hdc.exe` | 5.23 MB |
| `avdevice-63.dll` | 4.78 MB |
| `avutil-61.dll` | 2.91 MB |
| `swscale-10.dll` | 2.31 MB |
| `swresample-7.dll` | 0.72 MB |
| 其余 25 个文件 | 约 4.9 MB |
| **合计** | **200.29 MB** |

FFmpeg 七件套合计约 **183.6 MB**，占 stage 的 **91.7%**。**这就是「82 MB 的全量更新包」的唯一原因**——项目本身、UI 框架、hdc 加起来不到 17 MB。

无 `coreclr.dll` → 确认当前是**框架依赖**发布。

### 5.2 自包含 vs 框架依赖（实测对比）

| 模式 | 文件数 | stage 体积 | nupkg（同参数推算） |
| --- | ---: | ---: | --- |
| 框架依赖（当前默认） | 34 | 200.29 MB | 82.09 MB（实测） |
| 自包含（`-SelfContained`） | 491 | **358.59 MB** | 原始 **+158.3 MB** |

**结论：体积不是选自包含的理由，反过来说也不能因为它「省 158 MB」就选自包含。** 真正的判断依据是运行时依赖：

- **框架依赖**：用户机器必须已装 **.NET 8 Desktop Runtime**。没装 → 双击后毫无反应或报缺 framework。用 `vpk --framework net8-x64-desktop` 可以让 Setup 自动补装，但**装运行时会弹 UAC 提权**，与「免管理员」目标直接冲突。
- **自包含**：用户什么都不用装，Setup 全程不需要管理员。代价是 +158 MB 原始体积。

> 综合判断：既然「免管理员」是硬需求，**正式版应选自包含**，并接受 200→358 MB 的体积；而由于 FFmpeg 本体已经 183 MB，这 158 MB 在压缩后的实际增量约 55–70 MB，对 82 MB 的下载量来说是 +70% 左右，可接受。**注意：自包含包尚未跑过 `vpk pack`，nupkg 实际大小未测**（只测了 stage 体积）。M6 实施时应先跑一次 `build-release.ps1 -SelfContained` 拿到真实数字。

---

## 六、应用内检查更新 POC

### 6.1 工程

`src\UpdatePoc\UpdatePoc.csproj`（net8.0 控制台，`<PackageReference Include="Velopack" Version="1.2.161" />`）。
**刻意不加入 `HdcMirror.sln`**：它是验证工具，不该污染主线构建产物，也不该被 `dotnet build sln` 拖着一起还原 NuGet。

### 6.2 四个必须知道的 API 事实（编译器 + 官方 XML 文档核实）

```csharp
// 1. GithubSource：后两个参数有默认值，三参写法成立
new GithubSource(string repoUrl, string accessToken = null, bool prerelease = false, IFileDownloader downloader = null)

// 2. UpdateManager 构造：source 必填，options/locator 可空
new UpdateManager(IUpdateSource source, UpdateOptions? options = null, IVelopackLocator? locator = null)

// 3. 检查与下载
Task<UpdateInfo?> CheckForUpdatesAsync()
Task DownloadUpdatesAsync(UpdateInfo, Action<int> progress, CancellationToken ct)

// 4. 应用
void ApplyUpdatesAndRestart(VelopackAsset? toApply = null, string[]? restartArgs = null)
void ApplyUpdatesAndExit(VelopackAsset? toApply = null)
```

踩坑点（照抄网上示例会编译失败的地方）：

| 容易写错 | 实际 |
| --- | --- |
| `info.TargetIsDowngrade` | 是 `info.IsDowngrade` |
| `info.DeltasToTarget.Count` / `.Count()` | `DeltasToTarget` 是属性且非集合，直接插值最稳 |
| `mgr.Channel` 读当前通道 | `Channel` / `DefaultChannel` / `IsNonDefaultChannel` 都是 **protected**，外部不可读 |
| Velopack 自带异常有很多 | 只有 3 个：`NotInstalledException` / `ChecksumFailedException` / `AcquireLockFailedException`；**网络错误都是 `System.Net.Http.HttpRequestException`** |

### 6.3 实跑取证（占位仓库 `https://github.com/PLACEHOLDER_OWNER/hdc-mirror`）

原始日志落在 `tools\pack\updatepoc-run.log`（每次运行覆盖）。核心输出：

```
[1A] new UpdateManager(new GithubSource(...))  —— 真实部署路径
     IsInstalled       : False
     AppId             : (null)
     CurrentVersion    : (null)

[2] CheckForUpdatesAsync()   ── prod · 真实 locator（未经 Setup.exe 安装的开发态）
    检查抛出:
    类型 : Velopack.Exceptions.NotInstalledException
    消息 : This operation can not be performed in an application that is not installed.
           Please install the application and try again.

[1B] new UpdateManager(source, null, new TestVelopackLocator("HdcMirror","0.1.0", ...))
     IsInstalled : True   AppId : HdcMirror   CurrentVersion : 0.1.0

[2] CheckForUpdatesAsync()   ── diag · TestVelopackLocator（仅诊断，正式版不用）
    检查抛出:
    类型 : System.Net.Http.HttpRequestException
    消息 : Response status code does not indicate success: 404 (Not Found).

[4] HTTP 旁证
    api.github.com/.../releases/latest            → 404
      {"message":"Not Found","documentation_url":"...","status":"404"}
    github.com/.../releases/latest/download/releases.win.json → 404
      {"error":"Not Found"}
```

（诊断过程中还遇到过一次 `502 BadGateway` 空响应体，复测即恢复——属 GitHub 侧瞬时抖动，说明**超时与 5xx 必须按可重试处理**。）

### 6.4 三条可以直接写进正式版的判定

1. **`NotInstalledException` 是「未安装」的哨兵，不是错误。** 它只在开发态（`dotnet run` / `bin\Debug` 直接跑）出现。正式版必须先判断 `manager.IsInstalled`，为 `false` 就直接跳过检查、连异常都不要触发。
2. **仓库不存在 / 无 Release / 断网，全部表现为 `HttpRequestException`（404 / 超时）。** 消息里只有 HTTP 状态码，没有业务语义——所以**不能靠 Message 判断原因**，只能整体归类为「本轮检查无结果」，静默写日志。
3. **检查更新必须先经 `Setup.exe` 安装才有意义。** 这也意味着 §10 的「发版检查清单」里必须包含「装一遍 Setup 再测更新」，否则永远测不出真实路径。

### 6.5 `TestVelopackLocator` 的用途（别用错）

官方 XML 原文：*mock / test implementation… can be used to verify that your application is able to find and prepare updates from your chosen update source **without actually having an installed application**. This could be used in a CI/CD pipeline, or unit tests.*

- 它**可以**用在 CI 里做「清单能否解析、包能否下载」的冒烟测试。
- 它**不能**在生产代码里替代真实 locator —— 它不校验包完整性、不处理安装目录。
- 构造第 2 参是**语义版本字符串**，传目录会抛 `ArgumentException: '...' is not a valid semantic version`。

---

## 七、失败策略矩阵（决定「检查失败要不要打扰用户」）

| 情形 | 异常类型 | 自动检查（启动时） | 手动检查（用户点按钮） |
| --- | --- | --- | --- |
| 开发态未安装 | `NotInstalledException` | 不发起（先判 `IsInstalled`） | 提示「当前为开发版本，不支持更新」 |
| 仓库 404 / 无 Release / 断网 / 超时 | `HttpRequestException`、`TaskCanceledException` | **完全静默**，只写日志 | 提示「检查失败，请稍后重试」 |
| 用户取消 | `OperationCanceledException` | 静默 | 静默 |
| 另一个进程正在更新 | `AcquireLockFailedException` | 静默 | 提示「另一实例正在更新」 |
| 下载包校验失败 | `ChecksumFailedException` | 静默 + 删包 | 提示「安装包损坏，请重试」 |
| 有更新 | 无异常，`UpdateInfo != null` | **必须提示**（唯一允许打扰的情况） | 提示并给「立即更新」 |
| 无更新 | `UpdateInfo == null` | 静默 | 提示「已是最新版本」 |

一句话原则：**自动检查只在「有更新」时出声；手动检查无论成败都给回音。**

---

## 八、GitHub Releases 上传前置

> ⚠️ 本阶段**未创建任何远程仓库、未推送、未建 Release**。以下为前置条件说明，实际上传需本人明确许可。

### 8.1 仓库与 tag

- 仓库：`https://github.com/<owner>/hdc-mirror`（`docs/06-命名与仓库信息.md` 里也是占位 `<owner>`）
- 代码里 `GithubSource` 的 URL 必须与**实际仓库**完全一致，改一处即可（建议集中成一个常量，见 §9.1）。
- tag 规则：`v<Version>`，例如 `v0.1.0`。**tag 名不参与更新判定**，判定靠 `releases.win.json` 里的 `Version` 字段；但规范化 tag 便于人读与回滚。
- `prerelease` 参数为 `false` 时，`GithubSource` 读的是 `/releases/latest` —— 它指向**最新一个非预发布 Release**。所以：
  - 预发布（Pre-release 勾选）**不会**被客户端看到，可用于灰度；
  - **不要**把两个不同版本的资产塞进同一个 Release（`latest` 只认一个 Release 的附件集合）。

### 8.2 每个 Release 必须上传的资产

依 `releases.win.json` 与 `assets.win.json` 逐条对照：

| 资产 | 依据 | 缺失后果 |
| --- | --- | --- |
| `releases.win.json` | `GithubSource` 的第一跳 | 检查更新直接 404 |
| `HdcMirror-<ver>-full.nupkg` | `releases.win.json` 的 `FileName` + `SHA256` | 检查能过、下载 404 或校验失败 |
| `HdcMirror-win-Setup.exe` | `assets.win.json` 的 `Installer` | 新用户无从安装（更新逻辑本身不受影响） |
| `assets.win.json` | 资产索引 | 部分客户端/工具链会读 |
| `RELEASES` | v1 兼容 | 老客户端可能读不到（本项目都是 v2 客户端，影响小但一并传） |
| `HdcMirror-win-Portable.zip` | `assets.win.json` 的 `Portable` | 只是绿色版缺失 |

**上传方式**：Release 附件（Assets），不是把文件塞进 git 仓库。**82 MB 的 nupkg 绝不能进 git**——会把仓库撑爆且永久留在历史里。

### 8.3 增量（delta）包的条件

- `vpk pack` 只有在 `--outputDir`（dist）里**已存在更低版本**的 nupkg 时才能算出 delta。本次因为 `-CleanDist` 清空过，只产出 `Full`。
- 所以：**跨版本发版时不要清 dist**，让 `HdcMirror-0.1.0-full.nupkg` 留在里面，打 0.1.1 时才会多出 `HdcMirror-0.1.0_to_0.1.1-delta.nupkg`，`releases.win.json` 会同时列出 `Deltas` 与 `Full`。
- delta 包**也要上传**（同属 `releases.win.json` 引用的资产），否则客户端回退到全量下载。
- 一个发布目录会随版本累积，建议 M6 之后定期归档（例如把 ≥N 个版本前的包移到 `dist\_archive`），但**当前版本与上一版本必须保留**。

### 8.4 发 Release 的两种方式

| 方式 | 说明 |
| --- | --- |
| 手工 Web 上传 | 简单，但每次要拖 6 个文件，易漏（尤其漏 nupkg） |
| `vpk upload github` | `vpk upload github --repoUrl <url> --token <TOKEN> --publish --releaseName "HdcMirror 0.1.0" --tag v0.1.0 --outputDir dist` —— **会让 token 出现在命令历史/日志里**，且本阶段禁止推送，故未采用也未验证 |

> 建议 M6 采用「手工上传 + 上传后逐项核对 §8.2 表格」的方式，把自动化留到流程稳定之后。任何自动上传脚本都必须在**人工触发**下运行，不得挂 CI 自动发布。

---

## 九、接线方案（**补丁建议，本次未实施**）

> 以下全部是建议。硬约束要求不改 `MainWindow.xaml(.cs)`、`MirrorWindow.*`、`Resources.resx`，且本次可写范围只到 `src\UpdatePoc\`、`tools\pack\`、`docs\`，所以**一行都没落地**。真正实施时建议作为 M6 的独立任务。

### 9.1 第一步（必须）：在入口接入 `VelopackApp.Build().Run()`

这是所有事情的前提，没有它装好的包**无法自我更新**（`vpk` 会直接 FTL 拦住）。

**方案 A（推荐，vpk 的静态检查能识别）**：新增 `src\HdcMirror.App\Program.cs`，并在 `HdcMirror.App.csproj` 加 `<StartupObject>HdcMirror.App.Program</StartupObject>`。

```csharp
// 新增文件：src\HdcMirror.App\Program.cs
using System;
using Velopack;

namespace HdcMirror.App;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // 必须最先执行：它负责处理 Setup.exe 传入的 --veloapp-install / --veloapp-uninstall /
        // --veloapp-updated 等钩子参数，这些分支会自行 Exit，不会走到界面代码。
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
```

csproj 片段：

```xml
<PropertyGroup>
  <!-- App.xaml 会生成一个 Main，这里显式指定入口，避免 CS0017 双入口 -->
  <StartupObject>HdcMirror.App.Program</StartupObject>
</PropertyGroup>
```

**方案 B（不改 csproj，但未验证能被 vpk 识别）**：在 `App.xaml.cs` 的构造函数最前面调 `VelopackApp.Build().Run();`。功能上等价（仍在任何窗口创建之前执行），但 `vpk` 扫的是**入口方法**，构造函数里的调用很可能识别不到，仍要 `--skipVeloAppCheck`。**本 POC 未验证该组合，实施时若走 B 必须实测「安装后能否自更新」。**

### 9.2 新增 `Services\UpdateService.cs`（建议文件，不改现有任何文件）

```csharp
// 建议新增：src\HdcMirror.App\Services\UpdateService.cs
using System;
using System.Threading;
using System.Threading.Tasks;
using Velopack;
using Velopack.Sources;

namespace HdcMirror.App.Services;

/// <summary>封装 Velopack：检查 / 下载 / 应用。所有异常在此消化，不向上抛。</summary>
internal sealed class UpdateService
{
    // ★ 唯一需要与真实仓库对齐的常量
    private const string RepoUrl = "https://github.com/PLACEHOLDER_OWNER/hdc-mirror";

    private readonly UpdateManager _manager;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public UpdateService()
    {
        // accessToken: null（公开仓库）；prerelease: false（预发布不参与）
        _manager = new UpdateManager(new GithubSource(RepoUrl, null, false));
    }

    /// <summary>开发态（非安装）为 false，此时任何检查都会抛 NotInstalledException。</summary>
    public bool IsInstalled => _manager.IsInstalled;

    /// <summary>上次已下载、等待下次启动安装的版本；无则 null。</summary>
    public VelopackAsset? PendingRestart => _manager.UpdatePendingRestart;

    /// <summary>检查更新。失败一律返回 null，由调用方决定要不要说话。</summary>
    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (!IsInstalled)
        {
            return null;
        }

        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _manager.CheckForUpdatesAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            // 404 / 断网 / 超时 / 未安装 / 更新锁被占：都是「本轮没有结果」，不是故障
            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>下载指定更新，进度 0..100。</summary>
    public async Task<bool> DownloadAsync(UpdateInfo info, Action<int> progress, CancellationToken ct = default)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _manager.DownloadUpdatesAsync(info, progress, ct).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (IsExpected(ex))
        {
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>立即应用并重启进入新版本。会结束当前进程。</summary>
    public void ApplyAndRestart(UpdateInfo info)
        => _manager.ApplyUpdatesAndRestart(info.TargetFullRelease);

    /// <summary>已下载、等下次启动再装（不打断当前投屏）。</summary>
    public void ApplyOnNextStart()
        => _manager.ApplyUpdatesAndExit(null);

    private static bool IsExpected(Exception ex) => ex is
        Velopack.Exceptions.NotInstalledException or
        Velopack.Exceptions.AcquireLockFailedException or
        Velopack.Exceptions.ChecksumFailedException or
        System.Net.Http.HttpRequestException or
        TaskCanceledException or
        OperationCanceledException;
}
```

> 注意 `ChecksumFailedException` 被归到「预期失败」里返回 `false`——但要区分对待：它意味着**下载确实坏了**，手动检查时应提示「安装包损坏，请重试」，自动检查时静默。调用方拿到 `false` 时自行判断场景。

### 9.3 启动时机（`App.xaml.cs`）

**位置**：`App.OnStartup` 末尾，`CreateTrayIcon()`（`App.xaml.cs:70`）之后。
**时机**：**延迟 5–10 秒**再发起。

理由（与本项目核心痛点直接相关）：启动阶段正是 HDC 握手 + 首帧解码最吃 CPU 和带宽的时候，此刻再开一个 HTTPS 连接去 GitHub，会拖慢首帧。M4 已经把输入延迟压到极限，不能在这里还回去。

```csharp
// 建议追加在 App.OnStartup 的 try 块内、CreateTrayIcon(); 之后
_ = Task.Run(async () =>
{
    // 让首帧和 HDC 握手先跑完
    await Task.Delay(TimeSpan.FromSeconds(8)).ConfigureAwait(false);
    await CheckForUpdatesOnStartupAsync().ConfigureAwait(false);
});
```

要求：
- 整个任务**不得阻塞** `OnStartup`，也不得 `await` 在 UI 线程上。
- 结果回 UI 时用 `Dispatcher.Invoke`。
- 已在投屏时（`MirrorWindow` 活着）**只提示不自动下载**——下载 82 MB 会抢带宽，直接毁掉投屏流畅度。

### 9.4 托盘菜单（`App.xaml.cs:164` `CreateTrayIcon`）

在「关于」之后、「退出」之前插一项：

```csharp
menu.Items.Add(CreateTrayMenuItem(Strings.Tray_CheckUpdate, OnCheckUpdateClick));
```

- 需要新字符串键 `Tray_CheckUpdate`（以及 §9.7 的对话框文案）→ **要改 `Resources.resx` 与 `Resources\Strings.cs`**，本次未做（受硬约束）。
- 文案集中走 `Strings`，符合项目「任何 .xaml / .cs 不允许出现中文字面量」的既有约定（`Strings.cs` 头部注释明写）。

### 9.5 关于页（`Views\AboutPage.xaml:9`）

`AboutPage.xaml` 的注释里已经预留了 M6 计划：「版本号…检查更新按钮」。所以在 `<StackPanel Margin="24">` 里加：

```xml
<Button
    x:Name="CheckUpdateButton"
    Margin="0,16,0,0"
    HorizontalAlignment="Left"
    Click="OnCheckUpdateClick"
    Content="{x:Static res:Strings.About_CheckUpdate}" />
<TextBlock
    x:Name="UpdateStatusText"
    Margin="0,8,0,0"
    FontSize="12"
    TextWrapping="Wrap" />
```

`AboutPage.xaml.cs` 的构造函数（`AboutPage.xaml.cs:14` 之后）可顺便把 `UpdateService` 注入进来。这是**最自然的入口**：用户想知道版本，就一定想知道有没有新版。

### 9.6 更新流程的三种 UX（按场景选）

| 场景 | 流程 |
| --- | --- |
| **空闲时手动检查** | 检查 → 有新版：弹「发现 0.1.1，是否现在更新」→ 确认后下载（进度条）→ 完成 → `ApplyAndRestart` |
| **启动自动检查** | 静默检查 → 有新版：**托盘气泡 + 关于页红点**，不弹模态框。用户点进关于页才下载 |
| **正在投屏** | 只标记「有待更新」，**禁止**自动下载/重启。退出投屏后再提示 `ApplyAndRestart` |

`UpdatePendingRestart != null` 说明上次已经下好没装，此时启动流程可以直接 `ApplyOnNextStart()`（下次启动生效），或提示「重启即可完成更新」。

### 9.7 需要新增的字符串键（`Resources.resx` + `Strings.cs`）

| 键 | 建议文案 |
| --- | --- |
| `Tray_CheckUpdate` | 检查更新 |
| `About_CheckUpdate` | 检查更新 |
| `Update_Checking` | 正在检查更新… |
| `Update_UpToDate` | 已是最新版本 |
| `Update_FoundFormat` | 发现新版本 {0}，是否现在更新？ |
| `Update_DownloadingFormat` | 正在下载 {0}%… |
| `Update_Ready` | 下载完成，将重启应用以完成更新 |
| `Update_Failed` | 检查更新失败，请稍后重试 |
| `Update_ChecksumFailed` | 安装包校验失败，请重试 |
| `Update_DevBuild` | 当前为开发版本，不支持应用内更新 |

> 这一步必然要动 `Resources.resx`，与本次硬约束冲突，因此**留给 M6 单独授权**。

---

## 十、发版检查清单

### 10.1 改版本号（唯一一处）

`src\Directory.Build.props`：

```xml
<Version>0.1.1</Version>              <!-- ★ 唯一来源，脚本读它 -->
<AssemblyVersion>0.1.1.0</AssemblyVersion>
<FileVersion>0.1.1.0</FileVersion>
```

`AssemblyVersion` / `FileVersion` 不自动跟随 `Version`，三处都要改。改完 `AboutPage` 会自动显示新版本（它读 `AssemblyInformationalVersion` 并截掉 `+commit` 后缀）。

### 10.2 命令

```powershell
# 正式发版（不要在 dist 里留旧版本时清空它，否则出不了 delta）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack\build-release.ps1

# 同一版本号重打（调试用）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack\build-release.ps1 -SkipPublish -CleanDist

# 自包含（正式版应当用这个，见 §5.2）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack\build-release.ps1 -SelfContained
```

**正式发版时必须已经去掉 `-SkipVeloAppCheck`**（脚本里它只是可选开关，不传即启用校验）。若此时仍报 `Unable to verify VelopackApp is called`，说明 §9.1 没做，**不许用开关绕过**。

### 10.3 逐项核对

- [ ] `src\Directory.Build.props` 三处版本号已改、且与 CHANGELOG 一致
- [ ] `dotnet build src\HdcMirror.sln -c Debug` → **0 警告 0 错误**（`TreatWarningsAsErrors=true`，一个警告就是失败）
- [ ] `tools\pack\build-release.ps1` 退出码 0，末尾打印 `✔ 打包成功`
- [ ] `dist\` 里 `HdcMirror-win-Setup.exe`、`HdcMirror-<ver>-full.nupkg`、`releases.win.json`、`assets.win.json`、`RELEASES` 齐全
- [ ] `releases.win.json` 里 `Version` 与 `Directory.Build.props` 一致，`SHA256` 非空
- [ ] **装一遍 Setup.exe 实测**：关于页版本号 = 新版本；`%LOCALAPPDATA%\HdcMirror\packages\` 下有对应 nupkg
- [ ] 在已安装状态下点「检查更新」→ 应返回「已是最新」
- [ ] git tag `v<版本>`（**需本人许可**）
- [ ] GitHub Release：tag `v<版本>`、**非** Pre-release、附件 = `dist\` 全部文件（**nupkg 必不可少**）
- [ ] 浏览器验证 `https://github.com/<owner>/hdc-mirror/releases/latest/download/releases.win.json` 内容正确
- [ ] 用**上一版**装好的客户端实测升级：能看到提示 → 下载 → 重启 → 版本号变了
- [ ] 跑 `tools\pre-push-check.ps1` 三道闸（隐私/样本/密钥），退出码 0（见 `AGENTS.md` 十六）

> 最后两项是整条链路唯一的真验证。前九项全绿也不能替代「装一台旧版、升一次」。

---

## 十一、本次交付物与改动范围

| 路径 | 性质 | 说明 |
| --- | --- | --- |
| `tools\pack\build-release.ps1` | 新增 | 打包脚本，六步，7 个参数 |
| `tools\pack\updatepoc-run.log` | 生成物（运行时） | POC 每次运行的取证日志，覆盖写 |
| `tools\pack\stage\` | 生成物 | publish 输出，34 文件 / 200.29 MB |
| `tools\pack\dist\` | 生成物 | 打包产物，6 文件 / 253.44 MB |
| `src\UpdatePoc\UpdatePoc.csproj` | 新增 | net8.0 控制台，Velopack 1.2.161，**不入 sln** |
| `src\UpdatePoc\Program.cs` | 新增 | 308 行，四段式取证 |
| `docs\14-更新通道POC.md` | 新增 | 本文 |

**未触碰**：`src\HdcMirror.App\`、`src\HdcMirror.Core\`、`src\HdcMirror.Decode\`、`src\HdcMirror.sln`、`src\HdcMirror.App\Resources\Resources.resx`、`.git`。

**关于「不破坏现有构建」**：`HdcMirror.sln` 只含 Core/Decode/App 三个工程，`UpdatePoc` 刻意未加入，因此 `dotnet build src\HdcMirror.sln -c Debug` 的输入集合与 M4 完全一致。实测 **0 个警告 / 0 个错误**。运行中的 `HdcMirror` 进程（占用 `bin\Debug\HdcMirror.exe`）未被终止，也未造成构建失败——Debug 构建只重写 `HdcMirror.dll`，不覆盖被锁的 exe。

> **时点说明（重要）**：本次验证期间，同一工作区还有**并行会话**在修改 `src\HdcMirror.App\`（`Resources.resx`、`Strings.cs`、`MainWindow.xaml(.cs)`）并新增 `src\AudioPoc\`。这些改动**不是本任务产生**，本任务也未覆盖它们。上述 `0 个警告 / 0 个错误` 是 2026-11-11 04:11 对该时刻工作树的实测结果；若并行改动继续演进，请以最新一次构建为准。

---

## 十二、未验证项与风险（诚实清单）

| # | 未验证 | 为什么 | 影响 |
| --- | --- | --- | --- |
| 1 | **真实 GitHub Release 下的检查成功路径** | 无远程仓库、无 Release（本人要求未许可） | 若清单格式与 `GithubSource` 期望不符，会在 M6 首次发版才暴露 |
| 2 | **增量（delta）升级实测** | 本次 `-CleanDist`，只产出 Full | 不知道真实 delta 体积；「老用户只下几 MB」这个卖点尚未证实 |
| 3 | **Setup.exe 实装 + 静默自更新** | 会写入用户目录、建开始菜单快捷方式，超出本次任务范围 | 免管理员、无 UAC 属**架构推断**（Velopack 装到 `%LOCALAPPDATA%`），未实测 |
| 4 | **自包含包的 nupkg 体积** | 只测了 stage（358.59 MB） | 实际下载量可能比 82 MB 高出 60–80% |
| 5 | **`VelopackApp` 在 `App` 构造函数里调用能否被 vpk 识别** | 需改 App 工程，超出可写范围 | 决定 §9.1 走方案 A 还是 B |
| 6 | **SmartScreen 首次运行的实际拦截文案** | 不做代码签名，且未实装 | 用户侧会看到「未知发布者」，需在发布说明里给出绕行步骤 |

**唯一真正的阻塞项**：GitHub 仓库不存在。第 1、2、3、6 项全部卡在这一个前提上——一旦授权建仓库并发出第一个 Release，这四项可以一次性验完。

---

## 十三、复现步骤（任何人可重跑）

```powershell
cd "D:\Documents\DSH Github 工作区\hdc-mirror"

# 1) 打包（POC 阶段需跳过 VelopackApp 校验）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\pack\build-release.ps1 -SkipVeloAppCheck -CleanDist

# 2) POC 取证（会真实请求 github.com 占位仓库，约 5–15 秒）
C:\dotnet8\dotnet.exe build src\UpdatePoc\UpdatePoc.csproj -c Debug --nologo
src\UpdatePoc\bin\Debug\net8.0\UpdatePoc.exe
#    → 原始日志：tools\pack\updatepoc-run.log

# 3) 主线构建不受影响
C:\dotnet8\dotnet.exe build src\HdcMirror.sln -c Debug --nologo

# 4) 自包含体积对比（可选，会在 tools\pack\_sc-probe 下产生 358 MB，测完记得删）
C:\dotnet8\dotnet.exe publish src\HdcMirror.App\HdcMirror.App.csproj `
  -c Release -r win-x64 --self-contained true -o tools\pack\_sc-probe -p:PublishReadyToRun=false
```

---

## 十四、给 M6 的一句话建议

**先做 §9.1（入口接 VelopackApp），再做 §9.2/§9.4/§9.5（服务 + 托盘 + 关于页），最后才建仓库发第一个 Release。** 顺序反了会得到「能打包、装得上、但永远更新不了」的包——因为 `vpk` 的校验一旦用 `--skipVeloAppCheck` 绕过，问题会被藏到用户升级那一刻才炸。
