# 参与贡献

感谢你有兴趣改进 HdcMirror。这是一个 Windows 桌面工具，用来通过 hdc 无线调试在电脑上镜像并操控
HarmonyOS NEXT 手机。手机端**不需要安装任何东西**，所以改动主要集中在电脑侧。

动手前请先读：

- [`README.md`](README.md) —— 功能、安装、常见问题
- [`RULES.md`](RULES.md) —— 本仓库的隐私红线与发布纪律（比本文更硬，冲突时以它为准）
- [`docs/07-架构决策记录.md`](docs/07-架构决策记录.md) —— 关键取舍的理由，避免重复走死路

---

## 一、环境要求

| 项 | 要求 |
| --- | --- |
| 操作系统 | Windows 10 2004（10.0.19041）及以上；音频功能需要该版本起步 |
| SDK | .NET 8 SDK（含 Windows Desktop 运行时） |
| Shell | PowerShell 5.1 及以上（仓库脚本用 `.ps1`） |
| 手机（可选） | HarmonyOS NEXT 5.0.0（API 12）及以上，开启开发者模式与无线调试 |

只用 C# / XAML，没有 Node、Java、Python 依赖。FFmpeg 的四个动态库与构建脚本已在仓库内
（`native/ffmpeg/win64/`），clone 后无需另行获取。

## 二、构建与自检

```powershell
# 构建整个解决方案
dotnet build src\HdcMirror.sln -c Debug

# 离线自检（不需要手机）
dotnet run --project src\HdcMirror.SelfTest\HdcMirror.SelfTest.csproj

# 热更新到本机已安装目录并启动（开发时最常用）
powershell -File tools\hotdeploy.ps1
```

**0 警告 0 错误是硬性出口标准**：`src/Directory.Build.props` 已设置
`TreatWarningsAsErrors=true`，有警告即构建失败，请不要用 `#pragma` 或改设置绕过。

打包安装包与升级包：

```powershell
powershell -File tools\pack\build-release.ps1
```

## 三、提交前必做的三件事

1. **构建通过**：`dotnet build src\HdcMirror.sln -c Debug` 退出码 0、无警告。
2. **自检通过**：`dotnet run --project src\HdcMirror.SelfTest\HdcMirror.SelfTest.csproj`。
3. **隐私与发布闸**：

   ```powershell
   powershell -File tools\pre-push-check.ps1
   ```

   退出码必须为 0。它包含三道检查：隐私扫描（真实 IP / 设备名 / MAC / 本机路径）、
   `samples/` 下不得有图片与视频、待提交内容里不得有密钥文件。

改动涉及界面或交互时，还要在本机实际运行确认改动生效 ——「编译成功」不等于「功能可用」。

## 四、代码约定

- 语言版本 C# 12，`Nullable` 与 `ImplicitUsings` 已全局开启。
- 注释与界面文案用中文；对外文案走 `src/HdcMirror.App/Resources/Resources.resx`（中文）与
  `Resources.en.resx`（英文），**两份必须同时补齐**，不要只改一边。
- **文本编码**：仓库内所有 `.cs` / `.xaml` / `.md` 都是 UTF-8。改含中文的文件请用支持 UTF-8 的编辑器
  或 `git` 自带的差异工具；不要用会按 GBK 解释的批量管道改写文件，历史上有过整文件变乱码的事故。
- 不引入新的第三方依赖，除非在 PR 描述里说明理由与体积影响（本项目对安装包体积敏感）。
- 不要提交 `bin/`、`obj/`、`data/`、日志、`.bak` 与本机调试产物。

## 五、文档同步

改功能必须同批改文档，不允许「代码先合、文档后补」：

| 改动类型 | 必须同步 |
| --- | --- |
| 新增/修改功能、参数、配置项 | `README.md`、`CHANGELOG.md` |
| 删除或改名功能 | 全文搜索该名称，README / CHANGELOG / 界面文案一处不留 |
| 升级、更换或新增第三方组件 | `THIRD-PARTY-NOTICES.md` |
| 隐私或对外承诺变化 | `README.md` 末尾「隐私与免责」 |

`CHANGELOG.md` 采用「最新在上」的版本段落，写清改了什么、为什么改、修的什么缺陷。

## 六、隐私红线

提交内容里**不得**出现：

- 真实设备地址（`192.168.*` / `10.*` / `172.16~31.*` 私网段）、端口、设备名、机型 SN、蓝牙 / Wi-Fi MAC
- 真实屏幕截图与录屏，含第三方 App 界面与个人内容
- 个人邮箱、手机号、真实姓名、本机用户目录路径、`Bearer` / `ghp_` / `sk-` 令牌、私钥

示例一律脱敏：地址写 `192.0.2.10`（RFC 5737 文档保留段）或 `<DEVICE_ADDR>`，设备名写 `My Phone`。
判定标准只有一条：**这条信息能不能定位到某一台具体的设备或某一个具体的人**。

`samples/` 只允许无隐私的二进制头（如 32 字节 SPS+PPS），截图、录屏、裸码流一律不入库。

## 七、Issue 与 Pull Request

**报缺陷**：用「缺陷报告」模板，务必写清版本号、Windows 版本、连接方式（无线 / USB）、
复现步骤与脱敏后的日志片段。日志在 `<安装根>\data\logs`。

**提功能**：先开 Issue 说明使用场景，避免写完才发现方向不对。

**提 PR**：

1. Fork 后从 `main` 切分支，一个 PR 只做一件事
2. 完成上面「提交前必做的三件事」，并在 PR 描述里贴出命令与结果
3. 填写 PR 模板，特别是「验证方式」与「未触碰的既有功能」两栏
4. 不要修改 `tools/pre-push-check.ps1`、`tools/privacy-scan.ps1` 等闸门脚本来让检查通过；
   确有必要改动，单独开 Issue 说明原因

维护者会在方便时回复。这个项目由个人维护，不承诺 SLA，但每个 PR 都会被看到。

## 八、不接受的方向

- 加入遥测、统计、账号体系或云端依赖（本项目的对外承诺是「不收集」）
- 在手机端安装任何 App 或服务，或要求 root / 解锁
- 在代码或文档里写针对审查者、AI 助手的指令性文字
- 放宽隐私规则、把闸门改松的 PR
