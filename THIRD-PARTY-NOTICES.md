# 第三方组件与许可

本程序自身代码以仓库根 `LICENSE` 为准。程序运行时还依赖以下第三方组件，各自许可见下。

---

## 一、FFmpeg（LGPL 2.1 或更高版本）

`native\ffmpeg\win64\` 下的四个动态库是本项目**自行编译**的极简构建：

| 文件 | 版本 | 许可 |
| --- | --- | --- |
| `avcodec-63.dll` | FFmpeg 9.0.1 | LGPL 2.1+ |
| `avutil-61.dll` | FFmpeg 9.0.1 | LGPL 2.1+ |
| `swscale-10.dll` | FFmpeg 9.0.1 | LGPL 2.1+ |
| `swresample-7.dll` | FFmpeg 9.0.1 | LGPL 2.1+ |

### 为什么是 LGPL 而不是 GPL

本构建**没有**启用任何 GPL 组件（未启用 x264 / x265 / libavformat 的 GPL 部分等）。`configure` 末尾输出的许可证标识为 `LGPL 2.1+`。因此随程序分发这四个库是允许的。

### 我们改了什么

只是编译选项裁剪，**没有修改 FFmpeg 源码**。完整参数见 `tools\ffmpeg\build-minimal.sh` 与 `docs\18-极简FFmpeg构建.md`，核心是：

```
--disable-everything --enable-decoder=h264 --enable-parser=h264 --enable-swscale
--disable-avformat --disable-avfilter --disable-avdevice
--disable-programs --disable-doc --disable-network --disable-autodetect
--enable-shared --disable-static
--extra-ldflags="-static -static-libgcc"
```

裁剪后只保留 H.264 解码器与 YUV→BGRA 转换，体积从 183.9 MB 降到 5.19 MB。

### 如何取得对应源码

- 上游源码：`https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz`
  SHA256：`CF38E0E2…37F635`（完整值见 `native\ffmpeg\win64\SOURCE.txt`）
- 构建脚本：`tools\ffmpeg\build-minimal.sh`（可复现，产物体积逐字节稳定）

### 如何替换这四个库（LGPL 要求的可替换性）

四个库均为**动态链接**，位于程序安装目录的 `native\ffmpeg\win64\`。你可以用自己的 FFmpeg 构建替换同名同 soname 的文件，替换后直接启动程序即可，无需重新编译本程序。soname 必须保持 `avcodec-63` / `avutil-61` / `swscale-10` / `swresample-7`，因为绑定层按文件名解析。

---

## 二、NuGet 依赖

| 组件 | 版本 | 许可 | 用途 |
| --- | --- | --- | --- |
| WPF-UI | 4.3.0 | MIT | 窗口与控件外观（Fluent 风格） |
| H.NotifyIcon.Wpf | 2.3.2 | MIT | 系统托盘图标与菜单 |
| Velopack | 1.2.161 | MIT | 安装包与自动更新 |
| Grpc.Net.Client | 2.66.0 | Apache-2.0 | 与手机端 scrcpy 服务通信 |
| Google.Protobuf | 3.27.3 | BSD-3-Clause | 同上，消息序列化 |
| FFmpeg.AutoGen | 9.0.1.1 | MIT | FFmpeg 的 C# 绑定（不含原生库） |

各许可证全文随 NuGet 包分发，也可在对应项目的代码仓库中获取。

---

## 三、运行期系统组件

以下组件由 Windows 自身提供，不随本程序分发：

- .NET 8 桌面运行时（`Microsoft.WindowsDesktop.App`，MIT）
- Windows Media Foundation / `Windows.Media.Audio.AudioPlaybackConnection`（蓝牙 A2DP 音频接收）
- CoreAudio / MMDevice API（音量与播放设备路由）
- DWM、User32、Shell32 等系统 DLL

---

## 四、上游协议参考

本程序的设备侧能力来自 HarmonyOS 手机自带的调试接口（`uitest` / `hdc`），**不向手机安装任何组件**。协议格式的调研结论见 `docs\02-上游实现分析报告.md` 与 `docs\04-POC验证报告.md`。
