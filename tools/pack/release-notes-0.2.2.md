0.2.2 —— 卸载器改成独立 EXE，安装与卸载共用同一套路径校验，清掉早期试验代码。

## 该下载哪个包？

| 你的电脑 | 下载这个 | 大小 |
| --- | --- | --- |
| **不确定 / 没装 .NET** | `HdcMirror-Setup-WithRuntime.exe` | 151 MB |
| 已装 .NET 8 桌面运行时 | `HdcMirror-Setup-NeedsDotNet8.exe` | 26 MB |

判断方法：设置 → 应用 → 已安装的应用，搜 `Microsoft .NET 8 Desktop Runtime`。
有 → 下 26 MB 那个；没有、或者懒得查 → 下 151 MB 那个（运行时打包在内，双击即装、装完直接能用）。

> 26 MB 那个包本身是 .NET 程序：机器上没有 .NET 8 桌面运行时，双击会直接提示缺少运行时、打不开。不要下错。
>
> 官方 Velopack 安装包 `HdcMirror-win-Setup.exe` 也在附件里，供脚本部署与软件内更新使用，界面没有安装位置选择。

## 这次改了什么

### 卸载

- 新增 `卸载 HdcMirror.exe`（独立单文件 EXE，913 KB，无控制台窗口）：双击 → 确认框 → 结束进程 → 调 Velopack 卸载 → 清安装目录 → 清注册表 → 清快捷方式，全程显示进度
- 安全校验：安装根只从「EXE 所在目录」或注册表 `InstallLocation` 取；目录名必须是 `HdcMirror`；必须有 `Update.exe` 与 `HdcMirror.exe`；不能是 `%LOCALAPPDATA%` / `%APPDATA%` / 用户目录 / `Windows` / `Program Files` / `%TEMP%` 本身
- 修掉 0.2.1 的严重缺陷：旧的 `卸载 HdcMirror.cmd` 会把 `%TEMP%` 的上一级（=`%LOCALAPPDATA%`）当安装根并递归删除，可能误删用户数据。脚本方式整体移除

### 安装

- 新增 `InstallPathGuard`：界面安装与 `--installto` 静默安装共用同一套校验（磁盘根 / 系统目录 / 用户数据目录 / 临时目录 / 超长路径 / 非空且非本程序 / 无写权限 全部拒绝）
- 路径框即时校验：不合格时提示行变红说明原因，「安装」按钮置灰
- 安装界面（语言选择 简体中文/English、用户说明与免责声明、路径选择与浏览、完成后的启动询问）进仓库
- 出两个安装包：`HdcMirror-Setup-NeedsDotNet8.exe`（26 MB，需 .NET 8）与 `HdcMirror-Setup-WithRuntime.exe`（151 MB，无需 .NET）

### 其它

- 数据全部在 `<安装根>\data`，卸载随安装目录一并清除
- 移除早期试验工程 `src/poc`、`src/poc2`、`src/AudioPoc`、`src/UpdatePoc`
- 仓库只保留最终图标与两张收款码，删掉设计过程图与调试截图

## 升级包

- `HdcMirror-0.2.2-full.nupkg`（17.12 MB）+ `HdcMirror-0.2.2-delta.nupkg`（6.21 MB，从 0.2.1 升级只需这个）
- 自包含线：`HdcMirror-0.2.2-full-WithRuntime.nupkg`（78.16 MB）
