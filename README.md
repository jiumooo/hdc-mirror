# 鸿蒙手机无线控制 · HdcMirror

> 通过 hdc 无线调试，在电脑上实时镜像并操控 HarmonyOS 手机。

一个自用的 Windows 桌面客户端：手机端**零安装**，只依赖系统自带的 hdc 无线调试。
画面走设备侧投屏服务，输入走 `uitest uiInput`，音频走蓝牙 A2DP，全部由电脑发起。

---

## 1. 它能做什么

| 能力 | 说明 |
| --- | --- |
| 无线投屏 | 局域网内自动发现已开启无线调试的手机，点一下就连上 |
| 鼠标操控 | 左键拖动 = 手指拖动，滚轮 = 上下滑动，右键 = 返回 |
| 键盘输入 | 字母、数字、中文直接敲；Esc / Ctrl+H / Ctrl+J / Ctrl+P / Ctrl+↑↓ 映射系统键 |
| 手机窗口 | 独立窗口，按设备分辨率锁比例，拖边改大小，永不越出桌面工作区 |
| 横竖屏 | 手机横过来，窗口自动转成横向，控制条移到右侧 |
| 断线重连 | 2→4→8→10 秒退避，10 分钟预算内自动恢复 |
| 设备信息 | 名称 / 品牌 / 型号 / 鸿蒙版本 / 内核 / 处理器 / 分辨率 / 内存（总量 + 已用）/ 地址 |
| 多设备 | 记住多台设备，下拉切换，「忘记」会同时断开连接 |
| 音频 | 电脑以蓝牙 A2DP Sink 身份接收手机音频，手机不再外放 |
| 画质 | 帧率 30/60、码率 4/8/16/24 Mbps 可调，下次连接生效 |
| 更新 | 查 GitHub Releases，有新版本时下载安装包并启动安装 |

## 2. 环境要求

| 项 | 要求 |
| --- | --- |
| Windows | Windows 10 2004（10.0.19041）及以上；音频功能依赖该版本引入的 `AudioPlaybackConnection` |
| 手机 | HarmonyOS NEXT，已开启「开发者选项 → 无线调试」 |
| 网络 | 同一局域网（建议 5 GHz；2.4 GHz 与蓝牙 A2DP 会互扰） |
| 运行时 | 自带 .NET 8 运行时，无需另行安装 |
| 蓝牙 | 手机与电脑已配对（仅音频需要） |

## 3. 安装

1. 下载 `HdcMirror-win-Setup.exe`，双击安装。
2. 首次启动会请求蓝牙相关权限（仅音频功能使用）。
3. 手机首次连接时，手机会弹「允许调试」，允许即可。

## 4. 快速上手

1. 手机打开无线调试，确认与电脑同一 Wi-Fi。
2. 启动 HdcMirror，主窗口会自动扫描，等下拉出现设备。
3. 选中设备 → 点「连接」→ 手机窗口出现画面。
4. 想听声音：点手机窗口控制条上的音频按钮，然后把手机控制中心的音频输出切到「电脑」。

## 5. 手机端零安装是怎么做到的

投屏服务由设备侧已有的 `uitest` 扩展机制提供：

```
hdc fport  ──►  本机端口  ──►  设备 abstract socket
uitest start-daemon singleness --extension-name libscreen_casting_*.z.so
```

- 扩展 `so` 由上游 hosScrcpy 提供，启动前按设备版本挑变体再推送；
- 输入不依赖该 `so`：触摸走 `uitest uiInput`（设备 8012 端口的 JSON-RPC），按键走 `uitest uiInput keyEvent`；
- 因此手机不需要安装任何 apk。

## 6. 数据存放位置

| 内容 | 路径 |
| --- | --- |
| 设置 | `%LOCALAPPDATA%\HdcMirror\settings.json` |
| 记住的设备（只存显示名 + 地址） | `%LOCALAPPDATA%\HdcMirror\devices.json` |
| 端口线索（忘记设备后仍保留随机端口） | `%LOCALAPPDATA%\HdcMirror\endpoints.json` |
| 日志（按天滚动，留 7 天） | `%LOCALAPPDATA%\HdcMirror\logs\hdc-mirror-YYYY-MM-DD.log` |
| 崩溃日志 | `%LOCALAPPDATA%\HdcMirror\logs\crash-YYYYMMDD-HHmmss.log` |

## 7. 网络访问

只访问以下目标，没有遥测、没有第三方统计：

- 本机 `127.0.0.1` 上的 hdc 端口转发；
- 局域网内手机的 hdc 端口（无线调试端口为随机高位端口，靠线索库而非固定扫描）；
- 只在检查更新时访问 `api.github.com` 与 Release 附件地址。

## 8. 隐私

- 不收集、不上传任何设备信息；设备清单只在本机保存「显示名 + 地址」。
- 不含序列号、UDID、账号、手机号。
- 日志里不写设备身份标识。

## 9. 项目结构

```
src/
  HdcMirror.Core/     协议与设备侧控制：hdc 封装、daemon 生命周期、gRPC 客户端、uitest 输入通道
  HdcMirror.Decode/   H.264 解码（零 WPF 依赖）
  HdcMirror.App/      WPF 界面：主窗口、手机窗口、设置、设备管理、托盘、更新
  HdcMirror.Audio/    独立进程：蓝牙 A2DP Sink 接收手机音频
  HdcMirror.SelfTest/ 离线自检（不需要手机、不联网）
docs/                 需求、架构、协议、POC 报告、缺陷记录、验收清单
tools/                隐私扫描、提交前检查、离线自检、打包脚本
```

## 10. 从源码构建

```powershell
# 需要 .NET 8 SDK（Windows）
dotnet build src\HdcMirror.sln -c Release

# 离线自检（不需要手机）
powershell -NoProfile -ExecutionPolicy Bypass -File tools\selftest.ps1
```

## 11. 已知限制

- 锁屏状态下投屏为黑屏（系统安全窗口保护），需先解锁再连。
- 设备侧 daemon 约 9 分钟会被断开一次，靠自动重连恢复。
- 鸿蒙不向普通 shell 暴露官方序列号与 SoC 型号（`errNum 106/1002`），处理器按「型号 → 芯片」本地表补齐，表里没有的型号只显示指令集架构。
- 音频接管后，手机自身的通话与铃声仍走手机。
- 音频首次连接约需 30 秒（系统 API 内部超时），失败后必须重启进程才能重试。

## 12. 版本

当前 0.1.0（M0~M4 已落地，M5 更新通道待仓库落地）。详细进度见 `docs/15-里程碑进度与验收清单.md`。

## 13. 致谢与免责

- 投屏协议与设备侧扩展 `so` 来自 [taxiao213/hnscrcpy](https://github.com/taxiao213/hnscrcpy)（Apache-2.0），本项目只做 Windows 端重写。
- 本项目为个人自用工具，与华为无隶属关系。依赖的闭源组件行为可能随系统升级变化，风险自负。
