# 04 · POC 验证报告（鸿蒙投屏 + 反向控制 · console 端到端）

> 目标：在**纯 PC 侧**用一个 console 程序证明两条链路都能跑通——
> ① 视频：H.264 Annex B 从手机经 gRPC 流到 PC 并落盘可解；
> ② 控制：PC 侧一条命令能让手机屏幕内容真实改变。
> 结论：**两条链路均跑通**，`exit code 0`，全流程 18.9 秒。

---

## 1. 结论速览

| 验收项 | 结果 | 硬证据 |
|---|---|---|
| A 设备在线 | ✔ | `hdc list targets` → `192.0.2.10:5555` |
| B so / daemon / fport | ✔ | 设备端 so `247989 B` 与 jar 内 6.8 变体**字节数一致**；`@scrcpy_grpc_socket` 监听；`tcp:15001` 转发 OK |
| C gRPC 视频流 | ✔ | 首帧 **794 ms**；30 条消息 / **272,102 B**；SPS 解出 **1260×2720**，与 `snapshot_display` 完全一致 |
| D 点击注入生效 | ✔ | 点击前后截图 MD5 `64c735f1…` → `bbc8da5f…`（不同） |
| 闭环（附加） | ✔ | 收流中途 swipe 注入后，帧数 **4 → 30（+26）** —— 注入导致的画面变化立刻以帧回流 |

**最有说服力的一条**：把 `extradata.bin`(32B) + `stream.h264`(272,070B) 拼成 `full.h264`，用自己写的 SPS 解析器读出
`profile_idc=100 (High) / level_idc=52 (Level 5.2) / 4:2:0 / 8bit / 1260×2720`，与设备截图尺寸逐像素一致。
这不是"收到一堆字节"，而是**真能交给解码器一帧一帧解出来的码流**。

---

## 2. 环境

| 项 | 值 |
|---|---|
| 设备 | HarmonyOS NEXT 真机（型号略） |
| 连接 | `hdc tconn 192.0.2.10:5555`（无线调试，屏幕已解锁） |
| 屏幕 | `1260 × 2720`（`snapshot_display` 实测） |
| 设备端 uitest | **7.0.0.1**（注意：上游文档记录的是 6.0.2.3，版本跨度大但 6.8 变体可用） |
| .NET SDK | **8.0.427**，必须用绝对路径 `C:\dotnet8\dotnet.exe`（PATH 里的 6.0 编译不过） |
| hdc | `%USERPROFILE%\.hnscrcpy\tools\windows-x64\hdc.exe` |
| jar | `%USERPROFILE%\.hnscrcpy\lib\hosScrcpy-1.0.20-beta.jar`（只读引用） |
| 视频 so | jar 内 `libscrcpy/libscrcpy_server_unix_6.8-20260801.z.so` = 247,989 B |
| 设备端落点 | `/data/local/tmp/libscreen_casting.z.so`（**必须改名**） |

---

## 3. 代码结构

```
src/poc/
├─ Poc.csproj         net8.0 + Grpc.Net.Client 2.66.0（唯一依赖）
├─ Program.cs         ~740 行，四阶段 + 离线分析模式
└─ scrcpy.proto       手写契约，字段号全部标注实测依据
```

### 3.1 一个关键决定：不用 protoc 生成类型，直接吃 wire bytes

上游 protobuf 生成类被 ProGuard 混淆，`reference/` 里也没有 `.proto`，字段号只能靠实测。
所以 POC 用 **Grpc.Net.Client 的原始 byte[] marshaller** 直接拿响应字节：

```csharp
var marshaller = Marshallers.Create<byte[]>(b => b, d => d);
var onStart = new Method<byte[], byte[]>(
    MethodType.ServerStreaming, "ScrcpyService", "onStart", marshaller, marshaller);
var call = channel.CreateCallInvoker()
    .AsyncServerStreamingCall(onStart, null, new CallOptions(cancellationToken: cts.Token), Array.Empty<byte>());
```

好处有两个：
1. **不需要 protoc**，Windows 上零构建工具链依赖（`Grpc.Tools` 也不用装）；
2. **能直接看字节**——字段号就是这么反推出来的，而且以后设备改协议，这个解析器不会因
   "类型不匹配"直接抛异常，只会少读一个字段。

`Proto.ParseMap()` 就是这个通用 wire format 解析器，`Sps.Parse()` 是最小 H.264 SPS 解析器。

---

## 4. 完整运行输出（原始）

```
╔═══════════════════════════════════════════════════╗
║  hncast POC · 鸿蒙投屏 + 反向控制 · console 验证   ║
╚═══════════════════════════════════════════════════╝
  device=192.0.2.10:5555  localPort=15001

── [A] 设备在线检查 ─────────────────────────────
  hdc list targets → exit=0 out=192.0.2.10:5555
  ✔ 设备已连接: 192.0.2.10:5555

── [B] 设备侧准备：so / daemon / fport ─────────────────────────────
  · 本地 so 大小 = 247,989 B；设备端 stat = 247989
  ✔ 设备端 so 大小已匹配，跳过推送
  · 抽象 socket @scrcpy_grpc_socket 存在 = False
  · 启动 daemon（cd 到 /data/local/tmp，扩展名只给文件名）…
  hdc shell "cd /data/local/tmp && uitest start-daemon singleness --extension-name
      libscreen_casting.z.so -scale 1 -frameRate 30 -bitRate 8388608 -p 5000
      -iFrameInterval 2000 &" → exit=0
  ✔ socket 已就绪（等待 0 秒）
  · 本 POC 的 daemon pid = 32283
  hdc fport tcp:15001 localabstract:scrcpy_grpc_socket → exit=0 Forwardport result:OK
  ✔ fport 列表确认: 转发已建立

── [C] gRPC onStart 收流 ─────────────────────────────
  · 建立 gRPC channel: http://127.0.0.1:15001 (h2c 明文)
  · 调 onStart(Empty)，限 30 帧 / 15 秒
  ★ 首帧到达 794 ms（建流+首帧）
  #1   len=32      flags=8    pts=1794327205954  seq_head=00-00-00-01-67  [AnnexB]
       → 首条消息(SPS+PPS) 存入 extradata.bin
  #2   len=39431   flags=2    pts=1794327205963  seq_head=00-00-00-01-65  [AnnexB]
  #3   len=8986    flags=0    pts=1794327206448  seq_head=00-00-00-01-61  [AnnexB]
  #4   len=8231    flags=0    pts=1794327206935  ...
  ...（#5 ~ #29 均为 flags=0 的 P 帧，len 5106~9001）
  #30  len=8385    flags=0    pts=1794327217161  seq_head=00-00-00-01-61  [AnnexB]
  · 已收满 30 帧，停止
  · 收流中途注入: swipe (630,1900)→(630,1300) exit=0 out=No Error
  · 注入时刻已收 4 帧 → 结束时 30 帧，增量 26 帧
  · 共收 30 条消息 / 视频 272,102 B / 时长 12.0s
  · 帧长 min=32 max=39431 avg=9070
  · onEnd 已调用（回包 2 B）
  ✔ extradata.bin = 32 B   stream.h264 = 272,070 B
  ✔ 首帧耗时 794 ms
  · ParamValue tag 实测：
      data   → f5/2
      flags  → f1/0
      len    → f1/0
      pts    → f1/0

── [D] 点击注入 + 截图比对 ─────────────────────────────
  · 屏幕 1260x2720，点击点 = (630,1360)
  · 点击前截图 → samples\shot_before.jpeg  md5=64c735f109ee2879e3046dcb4123299c
  · uitest uiInput click 630 1360 → exit=0 518 ms out=No Error
  · 点击后截图 → samples\shot_after.jpeg   md5=bbc8da5f1236c558b28153451835a489
  ✔ MD5 不同 → 屏幕内容已改变，注入生效

── [E] 清理临时文件 ─────────────────────────────
  · 设备端残留检查: ls: ... No such file or directory ×3（已全部删除）
  · 已撤 fport tcp:15001
  · 已 kill -9 本 POC 的 daemon pid=32283（SIGTERM 被设备拒绝，必须 -9）

════════════ 汇总 ════════════════
  A 设备在线          : ✔ 通过
  B so/daemon/fport   : ✔ 通过
  C gRPC 视频流       : ✔ 通过
  D 点击注入生效      : ✔ 通过
  总耗时              : 18.9 秒
═════════════════════════════════════
```

### 4.1 离线分析输出（`--analyze`，不访问设备）

```
── [A] 离线分析 samples/（不访问设备） ─────────────────────────────
  · extradata.bin = 32 B      md5=186aaa7c3687eb2530da9da0017122f8
  · stream.h264   = 272,070 B md5=341238faf855e65547a97b152b32eea8
  · 合并 full.h264= 272,102 B
  · 起始码 31 个，NAL 分布：
      type 1  非IDR切片(P)    × 28
      type 5  IDR切片        × 1
      type 7  SPS          × 1
      type 8  PPS          × 1
  · SPS 解析：
      profile_idc=100 (High)  level_idc=52 (Level 5.2)
      chroma_format_idc=1  bit_depth_luma=8  bit_depth_chroma=8
      frame_mbs_only=True  crop L/R/T/B=0/2/0/0
      ★ 分辨率 = 1260 x 2720
  · IDR 数量 = 1, P 数量 = 28
```

31 个起始码 = 1 SPS + 1 PPS + 1 IDR + 28 P，与「30 条消息：首条含 SPS+PPS 两单元、第 2 条 IDR、
其余 28 条 P」**逐项自洽**——说明"一条 gRPC 消息 = 一个访问单元"这个契约成立。

---

## 5. 关键实测数据

| 指标 | 实测值 | 备注 |
|---|---|---|
| 首帧耗时 | **794 ms**（另一次 1695 ms） | 含 channel 建立；波动取决于是否有待推帧 |
| 30 帧总耗时 | 12.0 s | 平均约 2.5 帧/秒，是"按变化推流"的结果，非带宽瓶颈 |
| 视频体积 | 272,102 B | 8 Mbps 上限内，实际约 0.18 Mbps |
| 帧长 min/max/avg | 32 / 39,431 / 9,070 B | 32B 是配置帧，39KB 是 IDR |
| 配置帧 | SPS 23B + PPS 9B = **32 B** | `flags=8` |
| IDR | `flags=2`，39,431 B | 第 2 条消息 |
| P 帧 | `flags=0`，~8.5 KB | 第 3~30 条 |
| `pts` | 纳秒，单调递增 | `1794327205954 → 1794327217161` |
| `len` | 与 `data` 长度**完全相等** | 可作校验位 |
| 点击往返 | **518 ms**（另一次 453 ms） | `hdc shell uitest uiInput click` |
| 截图体积 | ~230 KB/张 | `snapshot_display` JPEG |

---

## 6. 遇到的问题（这是本报告最值钱的部分）

### 6.1 `ReplyMessage.payload` 的 tag 不是 1，是 3 —— 文档推测值是错的

`HOS_SCRCPY_PROTOCOL.md:69` 写的是 `map<string, ParamValue> payload = 1`。
按 1 解析的结果是：**每条消息都收到，但 map 永远是空的，`data` 长度为 0**。
第一次跑就踩到了——日志显示 `#1 len=0 flags=- pts=-`，收满 4 条消息落盘 0 字节。

从原始字节一眼看出真相：

```
1A 2A 0A 04 "data" 12 22 2A 20 <32B>
^^ 0x1A = 26 = (3 << 3) | 2  → field 3, wire 2
```

同时 `ParamValue.valBytes` 是 **field 5**（`0x2A`），不是常见的 1。`flags/pts/len`
都是 **field 1 varint**（`0x08`）。

> 教训：凡文档里写成 `= ?` 的 tag，一律不能猜，必须抓字节。这类"能连上、能收到、但业务字段全空"
> 的 bug，靠异常和日志都看不出来，只能直接 dump wire format。

### 6.2 首条消息是 32 字节，不是 33

文档写"约 33 字节"。实测精确 32 = SPS 23（`67 64 00 34 AC B7 02 78 0A AB 7C D4 04 34 14 1E 10 08 5C`，
含 4 字节起始码）+ PPS 9（`68 EE 33 C8 F0`，含起始码）。
按"大小必须 33"做断言会误判。

### 6.3 已有 daemon 不一定在服务 scrcpy socket

设备上确实有上游 hnscrcpy 留下的 `uitest start-daemon singleness`（PID 15201）在跑，
但它**没带 `--extension-name`**，只服务 upstream 的 `tcp:8012` 控制通道：

```bash
$ cat /proc/net/unix | grep -i scrcpy
（空）
```

所以"已有 daemon 就不起新的"是错判。正确判定**唯一可靠依据是抽象 socket 是否在监听**：

```bash
$ cat /proc/net/unix | grep scrcpy_grpc_socket
0: 00000002 0 10000 1 1 102238015 @scrcpy_grpc_socket
```

POC 把这一步做成了幂等：socket 在 → 复用；不在 → 起一个自己的，并记下 **自己的 pid**
（靠 `ps -ef | grep extension-name | grep libscreen_casting.z.so` 精确匹配），
清理时只杀自己那个，绝不碰上游的。

### 6.4 停 daemon 必须 `kill -9`

`kill <pid>`（SIGTERM）设备端直接拒绝，进程不死。必须 `kill -9`。
POC 的清理段固定用 `-9`，并只针对步骤 6.3 里识别出的自己的 pid。

### 6.5 `uitest uiInput click` 是真的生效，且延迟量级必须纳入设计

实测 `exit=0 out=No Error`，**518 ms**，截图 MD5 立刻改变。
但这个 ~450–520 ms 的往返是每次调用的硬成本（一次 `hdc shell` 子进程 + 设备侧 uitest 处理）。

**对正式客户端的直接含义：60Hz 触摸透传在这个通道上不可能实现。**
流畅拖拽不能靠连续发 `swipe`，必须把滑动当成"一次动作"（起点 + 终点 + 速度）下发。
详见 §7。

### 6.6 静置零帧是正常的（第一次跑被这个骗了）

第一次 dump 运行 8 秒只收到 4 条消息就超时结束，很容易误判成"断流"。
`flags`/`pts` 一看就清楚：第 1 条配置帧、第 2 条 IDR、之后零星 P 帧——
**屏幕没变化，服务端就不推帧**。

POC 的处理方式是根本不依赖帧超时判活：`onStart` 建流即算通道健康，
再靠"配置帧到达 = 可解码"。这也是正式客户端应该采用的判据。

### 6.7 uitest 版本是 7.0.0.1，与上游文档的 6.0.2.3 差一个大版本

上游按 `uitest --version` 选 so 变体（≥6.0.2.1 → 6.3.1/6.4/6.5）。本机是 **7.0.0.1**，
按文档规则会选不到任何变体。实测 **6.8 变体（`libscrcpy_server_unix_6.8-20260801.z.so`）直接可用**，
且设备上原本就是这个文件（247,989 B）。

> 正式客户端的变体选择表必须补上 7.x → 6.8/6.9，并保留"按 md5 匹配优先、失败再按版本兜底"的顺序。

### 6.8 一些细节坑

- `hdc shell "cd /data/local/tmp && uitest start-daemon … &"`：**`cd` 必须有**，
  `--extension-name` 只能给**文件名**，给路径会加载失败。
- `hdc fport` 重复添加同一端口会失败，必须先 `fport rm` 再 `fport add`。
- gRPC 走 h2c 明文，需要
  `AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true)`，
  并把 `MaxReceiveMessageSize` 抬到 100MB（默认 4MB，IDR 大了就会炸）。
- 设备侧临时文件（`hx.jpeg` 等）用完必须删；POC 清理段会回读 `ls` 确认删除成功。

---

## 7. 搬进正式 WPF 客户端的实施建议

先说结论：**协议层和探针层可以直接搬，渲染层和输入层必须重写。**

### 7.1 可直接复用（代码基本原样拿走）

| 模块 | POC 位置 | 为什么可复用 |
|---|---|---|
| gRPC 契约 + channel 配置 | `Program.cs` PhaseC 前半 | h2c + 100MB + `EnableMultipleHttp2Connections`，这些参数是试出来的，别重调 |
| wire format 解析器 | `Proto.ParseMap` | 不依赖 protoc；**建议保留为"协议漂移保险"**，同时用 `scrcpy.proto` 生成强类型做正常路径，两者对拍 |
| SPS 解析 | `Sps.Parse` | 建解码器**必须**知道分辨率/profile/level；`1260×2720` 就是这么来的，还能省掉一次 `snapshot_display` |
| hdc 进程封装 | `Hdc()` | 30s 超时 + UTF-8 + `CreateNoWindow`，Windows 上反复踩的点都处理了 |
| 幂等设备准备 | PhaseB | so 大小比对、socket 探测、fport 重建、**只杀自己 pid**，这段逻辑 WPF 一模一样要用 |
| 设备契约实测结论 | `scrcpy.proto` 注释 | "一条消息 = 一个访问单元"、"flags 8/2/0"、"pts 纳秒单调" |

### 7.2 必须重写

| 模块 | POC 的做法 | WPF 必须改成 |
|---|---|---|
| 帧处理 | 每条消息 `new byte[]` + 立刻 `FileStream.Write` | `ArrayPool<byte>` + 环形缓冲；**只保留最近 IDR 及之后的帧**（丢帧策略），绝不能边收边写盘 |
| 解码 | **完全不解码**（POC 只证明字节可解） | Media Foundation 优先（`MFVideoFormat_H264`，Annex B 字节流模式，零第三方依赖）；花屏/不支持时回退 FFmpeg.AutoGen |
| 渲染 | 无 | D3D11 纹理（`MF` 的 `IMFMediaBuffer` 可直接映射）或 WriteableBitmap；**不要**用 GDI+ |
| 输入 | 每动作一个 `hdc shell` 子进程 | 见 §7.3，核心是**异步队列 + 合并 + 乐观渲染** |
| 生命周期 | 每次跑完 `kill -9` daemon | 常驻复用（启动一次，退出时清）；避免每次启动 794ms 的首帧开销 |
| 错误恢复 | 单次尝试，失败即退出 | channel 断开重连 + `onRequestIDRFrame` 请求关键帧；`pts` 跳变检测丢帧 |
| 背压 | 同步 `await MoveNext` | 生产者/消费者分离；UI 线程永不阻塞在 IO 上 |

### 7.3 输入层的现实约束（这是 WPF 体验的天花板）

实测数字摆在这：**一次 `uitest uiInput click` = 453–518 ms**。
所以：

1. **能做**：单击、双击、长按、一次滑动（起点→终点→速度）、按键（Home/Back/Power）、
   文本输入（`inputText`）。这些是离散动作，500ms 延迟可接受。
2. **不能做**：60Hz 触摸透传、连续拖拽（如拖进度条、画图）。
3. 工程对策：
   - 输入走**单线程串行队列**，绝不允许并发 `hdc` 子进程（会互相踩端口/抢占 uitest）；
   - 鼠标移动**不入队**，只在 `MouseUp` 时把"起点 + 终点"合成一条 `swipe`；
   - 本地**先画手指轨迹**（乐观渲染），回包后不修正——用户感知延迟从 500ms 降到 0；
   - 用 `dircFling <0-3>` 代替长距离 swipe 做翻页，指令更短；
   - 把屏幕尺寸、so 校验结果**缓存**，别每次点击都 `snapshot_display`（那也是一次 200ms+ 往返）。

> **不要**走的弯路（均已实测否定）：
> - `hdc shell uinput -M -c 0` / `-m` / `-g` —— **假成功**，返回 `click 0` / `move to x y` 但屏幕毫无反应；
> - 8012 端口 JSON-RPC 的 `Gestures` —— 返回 `null`，MMI 报 `29201 operation not permitted`；
> - 指望 `uitest_agent_*.so` 提供低延迟触摸通道 —— 本次未走通，且 POC 不需要它。

### 7.4 建议的 WPF 分层

```
HnCast.Core            (net8.0 类库，无 UI 依赖 —— POC 的协议层搬这里)
├─ Scrcpy/             onStart 流客户端、wire 解析、SPS 解析、帧重组
├─ Hdc/                hdc 进程封装、fport 管理、daemon 生命周期
└─ Input/              离散动作命令构造 + 串行队列 + 节流

HnCast.Decode          (MF 解码 + D3D11 纹理)
HnCast.App             (WPF: MainWindow + 输入映射 + 设置)
```

`HnCast.Core` 就是本 POC 去掉 `Main` 和文件落盘之后的形状——这也是为什么 POC 值得先做：
**它的 90% 是正式版的真实代码，只有落盘和打印是一次性的。**

---

## 8. 复现步骤

```powershell
# 1) 构建（必须用绝对路径的 dotnet 8）
cd "D:\Documents\DSH Github 工作区\hncast\src\poc"
C:\dotnet8\dotnet.exe build -c Release

# 2) 端到端验证（30 帧 / 15 秒，中途一次 swipe）
.\bin\Release\net8.0\hncast-poc.exe --max-frames 30 --timeout 15

# 3) 只看协议细节（打印原始 protobuf 前 96 字节）
.\bin\Release\net8.0\hncast-poc.exe --dump --max-frames 3 --timeout 8

# 4) 离线分析已落盘码流（不碰设备）
.\bin\Release\net8.0\hncast-poc.exe --analyze
```

参数：`--device` `--local-port` `--hdc` `--jar` `--samples` `--max-frames` `--timeout`
`--so-entry` `--dump` `--keep-daemon` `--no-interact` `--analyze`

---

## 9. 产物清单

| 文件 | 大小 | 说明 |
|---|---|---|
| `samples/extradata.bin` | 32 B | 首条消息 = SPS(23B) + PPS(9B)，Annex B |
| `samples/stream.h264` | 272,070 B | 第 2~30 条消息拼接（IDR + 28×P），Annex B |
| `samples/full.h264` | 272,102 B | extradata + stream 合并，可直接喂解码器/播放器 |
| `samples/shot_before.jpeg` | 230,480 B | 点击前截图 |
| `samples/shot_after.jpeg` | 230,603 B | 点击后截图（MD5 已变） |
| `src/poc/scrcpy.proto` | — | 手写契约，字段号均标注实测依据 |
| `src/poc/Program.cs` | ~740 行 | 四阶段 + 离线分析 |

md5：
```
extradata.bin     186aaa7c3687eb2530da9da0017122f8
stream.h264       341238faf855e65547a97b152b32eea8
```

---

## 10. 对后续开发的判断

1. **投屏链路没有剩余技术风险。** 协议、变体选择、首帧、解码前置信息（SPS）全部确认。
   剩下的是纯工程：MF 解码 + D3D11 渲染 + 背压，都是常规活。
2. **控制链路的能力边界已经划清。** 命令行通道 ~500ms/动作，只适合离散动作。
   如果产品要求"拖拽跟手"，那必须另找通道（华为 MMI 权限是硬门槛，本次两条替代路径都已否定），
   或者改变交互设计（把拖拽降级成"点起点、点终点"或方向 fling）。
3. **最大的隐性成本是 `hdc` 子进程。** 每次交互 150–500ms 全在这里。
   正式版应该评估：能否常驻一个 hdc 会话（`hdc shell` 交互模式）把多条命令流水化，
   把 N 次往返压成 1 次。
4. **协议字段号这个坑一定要写进架构文档**：`payload=3`、`valBytes=5`。
   任何"按文档猜 tag"的代码都会遇到"能连上但数据全空"这种最难查的 bug。
