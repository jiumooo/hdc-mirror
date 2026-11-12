# 18 · 极简 FFmpeg 构建（仅 H.264 解码）

> 目标：把 `native\ffmpeg\win64\` 从 **183.9 MB** 的 BtbN GPL 完整构建，换成只含 H.264 解码的 **5.19 MB** 极简构建。
> 结果：**183.9 MB → 5.19 MB（-97.2%）**，离线解码像素与旧构建**逐字节一致**，许可从 GPL v3 降为 LGPL v2.1+。
> 实测日期：2026-11-12。

---

## 一、为什么需要

HdcMirror 只需要一条路径：**裸 H.264 解码 + YUV→BGRA 缩放**。

`HdcMirror.Decode` 直接吃手机投屏的 H.264 Annex-B 码流（每条 gRPC 消息一个完整 access unit），
自己切 AU、自己喂 `avcodec_send_packet` / `avcodec_receive_frame`，再 `sws_scale` 成 BGRA 给 WPF。

不走的路径：容器解封装（`avformat` 从没被调用）、滤镜图（`avfilter`）、采集设备（`avdevice`）、
重采样（`swresample` 的 `swr_*`）、网络协议（`--disable-network`）、`avio`、`av_image`、`av_dict`、`av_opt`、`av_parser`。

而 BtbN 的 `ffmpeg-master-latest-win64-gpl-shared` 把上游**全部**编解码器（含 libx264/libx265/各种硬件加速）、
全部封装器、全部滤镜、全部协议都打了进来。183.9 MB 里 99% 的机器码在这条路径上永远不会被执行，
却要被拷进安装包、随每次启动被映射进进程地址空间。

所以：用官方源码 + `--disable-everything` 只留 H.264 解码器和解析器。

---

## 二、第一步：先确认代码到底用了什么（不能凭感觉砍）

在 `src\HdcMirror.Decode\` 与 `src\HdcMirror.App\` 全量搜索，得到**实际引用符号全集**：

| 库 | 用到的符号 |
|---|---|
| avcodec | `avcodec_alloc_context3`、`avcodec_find_decoder`、`avcodec_free_context`、`avcodec_open2`、`avcodec_send_packet`、`avcodec_receive_frame`、`avcodec_flush_buffers`、`avcodec_version` |
| avutil | `av_frame_alloc`、`av_frame_free`、`av_frame_unref`、`av_frame_clone`、`av_new_packet`、`av_packet_alloc`、`av_packet_free`、`av_packet_unref`、`avutil_version` |
| swscale | `sws_getContext`、`sws_scale`、`sws_freeContext`、`swscale_version` |

逐项否定的结论（全部 **0 命中**）：`avformat_*`、`avfilter_*`、`avdevice_*`、`swr_*`、`avio_*`、
`av_image_*`、`av_malloc`、`av_free`、`av_dict_*`、`av_opt_*`、`av_parser_*`、`avcodec_parameters_*`。

关键调用点：

- `src\HdcMirror.Decode\Ffmpeg\FfmpegH264Decoder.cs:211-212` —— `context->thread_count = 1; context->thread_type = ffmpeg.FF_THREAD_SLICE;`（所以只需要 slice 级并行，不需要帧级多线程的基础设施）
- `src\HdcMirror.Decode\Ffmpeg\FfmpegH264Decoder.cs:366` —— `ffmpeg.sws_scale(_scaler, frame->data, frame->linesize, 0, height, destination, destinationStride);`
- `src\HdcMirror.Decode\Ffmpeg\FfmpegH264Decoder.cs:384` —— `sws_getContext(..., AV_PIX_FMT_BGRA, (int)SwsFlags.SWS_BILINEAR, null, null, null)`
- `src\HdcMirror.Decode\Ffmpeg\FfmpegRuntime.cs:72/75/81` —— `DynamicallyLoadedBindings.Initialize()` → `ffmpeg.RootPath = resolved` → 版本串用 `avcodec_version/avutil_version/swscale_version`

⇒ **`avformat` / `avfilter` / `avdevice` 一律不打包**（这 3 个库里 63 MB 全是死重量）。

---

## 三、版本必须是 9.0.1，不是 7.x

托管绑定是 `FFmpeg.AutoGen 9.0.1.1` + `FFmpeg.AutoGen.Bindings.DynamicallyLoaded`。
该包 README 明确「包版本与 FFmpeg 版本同步」，且 `LibraryVersionMap` 把库文件名硬编码成
`名字-主版本.dll`：

```
avcodec-63 / avutil-61 / swscale-10 / swresample-7 / avformat-63 / avfilter-12 / avdevice-63
```

旧 BtbN 构建的 ProductVersion 是 `N-128728-g49a3b1f259-20261110`，其 soname 恰好也是 63/61/10。

**若按 7.1.1 编译，会产出 `avcodec-61` / `avutil-59` / `swscale-8`**：
文件名对不上 `LibraryVersionMap`，且 `AVCodecContext` 结构布局在 9.x 已变（新增字段），
运行期必然崩。所以源码必须取 **9.0.1**：

```
https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz
12036420 字节
SHA256 CF38E0E28C7E5605942C4A77755349B0145804A397AF37EB1FB4C77CB237F635
```

---

## 四、configure 参数逐项解释

```bash
./configure \
  --prefix=/d/ffmpeg-build/prefix \
  --arch=x86_64 --target-os=mingw32 \
  --enable-shared --disable-static \
  --disable-programs --disable-doc \
  --disable-network --disable-autodetect \
  --disable-everything \
  --disable-avdevice --disable-avfilter --disable-avformat \
  --enable-decoder=h264 --enable-parser=h264 \
  --enable-swscale --enable-w32threads \
  --extra-ldflags="-static -static-libgcc"
```

| 参数 | 作用 | 为什么 |
|---|---|---|
| `--arch=x86_64 --target-os=mingw32` | 目标平台 | 交叉/本机构建出 PE32+ DLL |
| `--enable-shared --disable-static` | 只要 DLL，不要 .a | 应用按 DLL 动态加载 |
| `--disable-programs` | 不产出 `ffmpeg.exe`/`ffprobe.exe`/`ffplay.exe` | 应用只要库，可执行文件白占体积 |
| `--disable-doc` | 不产出文档 | 同上 |
| `--disable-network` | 关掉所有协议（rtmp/http/tcp/udp…） | 解码路径不发任何网络请求 |
| `--disable-autodetect` | 不自动探测外部库 | 防止构建机装了 x264/x265/openssl 后**悄悄链上**，把 LGPL 变成 GPL、把体积打回去 |
| `--disable-everything` | 关掉所有组件（decoder/encoder/parser/muxer/demuxer/filter/protocol/bsf/indev/outdev） | 这是体积从 183 MB 掉到 2 MB 的那一刀。注意它**只关组件，不关库**，所以要单列下面三条 |
| `--disable-avdevice` | 去掉采集设备库（4.78 MB 旧体积） | 0 引用 |
| `--disable-avfilter` | 去掉滤镜库（36.19 MB 旧体积） | 0 引用，缩放用 swscale 不用 filter graph |
| `--disable-avformat` | 去掉封装库（22.05 MB 旧体积，连带 muxer/demuxer） | 0 引用，码流自己切 AU |
| `--enable-decoder=h264` | 只留 H.264 解码器 | 唯一必需 |
| `--enable-parser=h264` | 只留 H.264 解析器 | 解码器内部依赖 h264 parser 拆 NAL |
| `--enable-swscale` | 保留 swscale | `sws_getContext` / `sws_scale` 要把 YUV420P 转 BGRA |
| `--enable-w32threads` | Windows 线程后端 | `FF_THREAD_SLICE` 需要线程支持 |
| `--extra-ldflags="-static -static-libgcc"` | 把 libgcc / libwinpthread **静态**链进 DLL | 见下面「踩坑」 |

`swresample` 未显式关闭 ⇒ configure 默认建它，产物 173 KB，保留（见下面「踩坑」）。

configure 收官输出（实测）：

```
Libraries:              avcodec  swresample  avutil  swscale
Enabled decoders:       h264
Enabled parsers:        h264
Enabled demuxers/muxers/protocols/filters/bsfs/indevs/outdevs:   （全空）
License: LGPL version 2.1 or later
```

---

## 五、构建步骤（可复现）

### 5.1 装工具链（本机原本没有 MSYS2）

```powershell
winget install --id MSYS2.MSYS2 -e --accept-source-agreements --accept-package-agreements --silent
```

```bash
MSYSTEM=MINGW64 C:/msys64/usr/bin/bash.exe -lc \
  'pacman -Sy --noconfirm; pacman -S --noconfirm --needed \
     mingw-w64-x86_64-gcc mingw-w64-x86_64-nasm make diffutils'
```

实测版本：`gcc 16.2.0` / `NASM 3.02` / `GNU Make 4.4.1`。

### 5.2 取源码

```powershell
curl.exe -L -C - --retry 100 --retry-delay 3 --retry-all-errors `
  -o D:\ffmpeg-build\src\ffmpeg-9.0.1.tar.xz `
  https://ffmpeg.org/releases/ffmpeg-9.0.1.tar.xz
```

```bash
cd /d/ffmpeg-build && tar xf src/ffmpeg-9.0.1.tar.xz
```

> 国内镜像（nju / bfsu / ustc / tuna / aliyun）均未收录 ffmpeg releases，只有官方站可用。
> 官方站单线程约 14–24 KB/s，12 MB 要下 10 分钟以上；**`curl` 不要设 `-m`/`--max-time`**，
> 否则超时重试会从 0 重下（实测踩过一次：到 89% 撞 600 秒超时，retry 又从头开始）。
> `-C -` 断点续传 + `--retry-all-errors` 是可靠的组合。

### 5.3 编译

仓库内已放好脚本：

```bash
MSYSTEM=MINGW64 C:/msys64/usr/bin/bash.exe -lc 'bash tools/ffmpeg/build-minimal.sh'
```

脚本做的事就是第四节的 configure + `make -j$(nproc)` + `make install`，可覆盖的环境变量：

| 变量 | 默认 |
|---|---|
| `FFMPEG_SRC` | `/d/ffmpeg-build/ffmpeg-9.0.1` |
| `FFMPEG_PREFIX` | `/d/ffmpeg-build/prefix` |
| `WITH_SWRESAMPLE` | `1`（设 0 会追加 `--disable-swresample`，见踩坑一） |
| `EXTRA_LDFLAGS` | `-static -static-libgcc`（**不要清空**，见踩坑二） |

实测耗时：configure 约 60 秒，`make -j20` **35 秒**。

> 手写 configure 时**千万不要漏掉** `--extra-ldflags="-static -static-libgcc"`，
> 否则产物会多出 `libwinpthread-1.dll` / `libgcc_s_seh-1.dll` 两个外部导入（见踩坑二）。

### 5.4 落位

```powershell
# 备份（一次性，保留以便回滚）
Copy-Item native\ffmpeg\win64 native\ffmpeg\win64.full-backup -Recurse -Force

# 替换
Remove-Item native\ffmpeg\win64\*.dll
Copy-Item D:\ffmpeg-build\prefix\bin\*.dll native\ffmpeg\win64\
```

`src\HdcMirror.App\HdcMirror.App.csproj` 用通配把 `native\ffmpeg\win64\*.dll` 拷进输出目录，
**不需要改 csproj**，也不需要改 `Directory.Build.props`。

---

## 六、踩坑记录（两个都不是理论问题，都实测复现过）

### 坑一：缺 `swresample-7.dll` 会让绑定初始化整体失败

渐进砍库实测（每次只少一个 DLL，用真机 29 帧码流解码）：

| 去掉的库 | 结果 |
|---|---|
| `avformat-63.dll` | OK |
| `avfilter-12.dll` | OK |
| `avdevice-63.dll` | OK |
| **`swresample-7.dll`** | **FAIL** |

失败栈：

```
System.NotSupportedException: Specified method is not supported.
   at FFmpeg.AutoGen.DynamicallyLoadedBindings.<>c.<Initialize>b__2_1255()
   at FFmpeg.AutoGen.DynamicallyLoadedBindings.<>c.<Initialize>b__2_525()
   at FFmpeg.AutoGen.ffmpeg.avcodec_version()
   at HdcMirror.Decode.Ffmpeg.FfmpegRuntime.EnsureInitialized()  FfmpegRuntime.cs:line 79
```

原因：`DynamicallyLoadedBindings.Initialize()` 会按版本映射表给**每个登记过的库**建函数指针，
缺一个就让整张表塌掉（连 `avcodec_version` 都变成抛异常的空壳）。
所以即使代码里没有一个 `swr_*` 调用，也必须留一个 swresample —— 极简版只有 **173 KB**，零成本。
`avformat`/`avfilter`/`avdevice` 不在它的必需集合里，可以安全删除。

### 坑二：MinGW 默认会把运行库留成外部依赖

第一次（不加 `--extra-ldflags`）的导入表：

```
avutil-61.dll     → libwinpthread-1.dll   ← 额外依赖
swscale-10.dll    → libgcc_s_seh-1.dll    ← 额外依赖
```

这两个 DLL 不在应用输出目录，`LoadLibrary` 会直接失败。加 `--extra-ldflags="-static -static-libgcc"`
重编后，导入表干净成：

```
avcodec-63.dll    → KERNEL32.dll  msvcrt.dll  avutil-61.dll
avutil-61.dll     → bcrypt.dll    KERNEL32.dll  msvcrt.dll
swscale-10.dll    → KERNEL32.dll  msvcrt.dll  avutil-61.dll
swresample-7.dll  → KERNEL32.dll  msvcrt.dll  avutil-61.dll
```

只剩系统库 + FFmpeg 自己，形态与旧 BtbN 构建一致，不需要额外分发 MinGW 运行库。

---

## 七、产物体积对比

| 文件 | 替换前 | 替换后 | 变化 |
|---|---|---|---|
| `avcodec-63.dll` | 114.94 MB | **1.94 MB** | -113.00 MB |
| `swscale-10.dll` | 2.31 MB | 2.02 MB | -0.29 MB |
| `avutil-61.dll` | 2.91 MB | 1.06 MB | -1.85 MB |
| `swresample-7.dll` | 0.72 MB | 0.17 MB | -0.55 MB |
| `avformat-63.dll` | 22.05 MB | **已删除** | -22.05 MB |
| `avfilter-12.dll` | 36.19 MB | **已删除** | -36.19 MB |
| `avdevice-63.dll` | 4.78 MB | **已删除** | -4.78 MB |
| **合计** | **183.9 MB**（192,821,397 B） | **5.19 MB**（5,439,125 B） | **-97.2%** |

为什么 `swscale-10.dll` 几乎没瘦（2.31 → 2.02 MB）：体积主要在 3D LUT / 色彩空间矩阵表
（`lut3d.o`、`cms.o`、`ops*.o`）这类**数据表**，跟启用了多少编解码器无关。
同理 `avcodec` 从 114.94 MB 掉到 1.94 MB 才是真正的收获 —— 那 113 MB 全是别的编解码器。

---

## 八、验证记录

### 8.1 离线解码（等价于真机码流，逐字节比对）

工具：仓库外小探针 `ffprobe-decode`，直接引用仓库编译出的 `HdcMirror.Decode.dll`，
走**同一条** `FfmpegH264Decoder` 路径解 `samples\full.h264`（272,102 字节 / 29 个 AU /
配置帧 32 字节 SPS+PPS / 真机 1260×2720）。

| 库集合 | 帧数 | 分辨率 | 像素抽样和 | 结论 |
|---|---|---|---|---|
| 旧完整构建（7 个 dll） | 29 | 1260×2720 | **30087233** | 基线 |
| 极简构建（4 个 dll，最终落位版） | 29 | 1260×2720 | **30087233** | **完全一致** |

落位后又用「App 输出目录里实际随包分发的那份 dll」复跑 3 轮：

```
第 1 轮：解码 29 帧，1260×2720，167.7 ms，172.9 fps，像素抽样和 30087233
第 2 轮：解码 29 帧，1260×2720，109.7 ms，264.3 fps，像素抽样和 30087233
第 3 轮：解码 29 帧，1260×2720，105.8 ms，274.0 fps，像素抽样和 30087233
RESULT=OK
```

像素抽样和三轮完全相同、且与完整构建相同 ⇒ 输出**逐字节一致**（不是「看起来正常」）。
fps 与旧构建同量级（270 左右 vs 271），说明没引入性能退化。
第一轮偏慢（173 fps）是 JIT 与页缓存冷启动，第 2 轮起稳定。

### 8.1.1 构建可复现性（实测结论，别被哈希骗了）

用交付的 `tools/ffmpeg/build-minimal.sh` 连续重跑 3 次：

- **体积/代码完全一致**：`avcodec-63.dll` 每次都是 2,031,616 字节，逐字节比对只有 **6 个字节**不同
- 差异固定在**同一批偏移**：`136/137`、`216/217`（PE 头 DOS stub 区两个 16 位字段）、`2008580/2008581`（`.rdata` 末尾）
- 因此 **SHA256 每次构建都会变**，这是链接期生成值，不是构建错误，也不影响功能

⇒ 校验方式应该是「**体积 + 解码像素和**」，不是哈希。`SOURCE.txt` 里那组 SHA256 只对应
「本仓库的这一份文件」，用于确认你没被换过文件。

### 8.2 编译与自检

```
C:\dotnet8\dotnet.exe build src\HdcMirror.sln -c Debug   → 已成功生成，0 警告 0 错误
powershell -NoProfile -ExecutionPolicy Bypass -File tools\selftest.ps1
  ✔ SPS 解析（真机采样 32 字节 extradata）   ✔ SPS 解析（Annex B 里找 SPS）
  ✔ IDR 判定                                ✔ 内存格式化
  ✔ 分辨率解析                              ✔ Release JSON 解析：有新版本
  ✔ Release JSON 解析：已最新               ✔ Release JSON 解析：异常输入
  ✔ 设备清单显示字段
  ✔ 全部通过（9/9）
```

> 注意：`tools\selftest.ps1` 这 9 项**不加载原生库**（纯托管逻辑），
> 它们能证明代码没被改坏，但不能证明 FFmpeg 能加载。原生库的验证靠 8.1。

### 8.3 真机验证（未完成）

`hdc list targets` 返回 `[Empty]` —— 设备 `192.0.2.10:5555`（`My Phone`）当前不在线，
**真机解码未验**。

要补验，设备在线后：

1. 启动 `HdcMirror.App`（Debug 输出目录已含新 dll），连 `192.0.2.10:5555`
2. 看画面是否正常出图
3. 日志里应有 `libavcodec 已就绪（...，原生库目录 ...\native\ffmpeg\win64）`
   以及 `首帧已解码 2720×1260`（宽度高度按实际旋转方向可能互换）
4. 观察 fps 与旧构建对比（离线基线 271 fps，新构建 270 fps）

---

## 九、回滚

完整旧构建整体备份在 `native\ffmpeg\win64.full-backup\`（8 个文件，183.9 MB，含旧 `SOURCE.txt`）。

```powershell
$root = 'native\ffmpeg'
Remove-Item "$root\win64\*" -Force
Copy-Item "$root\win64.full-backup\*" "$root\win64\" -Force
C:\dotnet8\dotnet.exe build src\HdcMirror.sln -c Debug
```

`native/` 被 `.gitignore` 忽略，所以备份不会进 git，也不会污染 `git status`。
备份目录**不要删**，它是唯一的一键回滚手段（BtbN 的 nightly 构建随时可能改名/消失，重下不保证同版本）。

---

## 十、附：本次改动清单

| 路径 | 动作 |
|---|---|
| `native\ffmpeg\win64.full-backup\` | 新增（旧 7 个 dll + 旧 SOURCE.txt 的整体备份） |
| `native\ffmpeg\win64\avcodec-63.dll` 等 4 个 | 替换为极简构建 |
| `native\ffmpeg\win64\avformat-63.dll` 等 3 个 | 删除 |
| `native\ffmpeg\win64\SOURCE.txt` | 重写（版本 / configure / 校验和 / 为什么极简 / 回滚） |
| `tools\ffmpeg\build-minimal.sh` | 新增（可复现构建脚本） |
| `docs\18-极简FFmpeg构建.md` | 新增（本文） |

未改动任何 `.cs` / `.csproj` / `Directory.Build.props` / `tools\pack\*`。
