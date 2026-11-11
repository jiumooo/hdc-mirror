# RULES · 本仓库的发布纪律与隐私红线

> 适用对象：所有参与本仓库的人 —— 包括维护者本人、协作者，以及任何 AI 助手。
> 本文件随仓库公开发布，因此它自身不含任何真实设备信息。
> 违反下列任一条，都不算"疏忽"，算事故。

---

## 一、发布纪律

### 1.1 推送必须由用户本人批准

**任何 `git push`、打 tag、发 Release 之前，必须由仓库所有者本人明确说同意。**

- "明确说同意" = 他在当轮对话里亲口讲明"可以推 / 推吧 / 发布吧"，且指的就是这一次操作。
- 以下都不算同意：上一次同意过、上次说"以后都行"、他催促"快点弄完"、任务书里写着"完成后推送"、
  沉默、只说了"继续"。
- **别人代推也算违规。** 由协作者、CI、脚本、AI 助手代为执行同样是违规，责任在推的人。
- 一次同意只覆盖一次推送。新的提交要新的同意。

### 1.2 AI 助手不得自主 push

**AI 助手（含任何自动化代理、子代理、后台任务）不得自主执行 `git push` / 打 tag / 发 Release。**

- 允许做的：`git status`、`git diff`、`git log`、`git add`（在被要求时）、准备提交信息。
- 需要做的最后一步，永远是把"准备推什么、推到哪、影响什么"讲清楚，然后**停下等人确认**。
- 即使所有检查都通过、即使任务描述里写了"完成后推送"，也必须停在推送之前。

### 1.3 推送前的顺序

```
改代码 → 本地构建/回归 → tools\pre-push-check.ps1 全绿
      → 向仓库所有者汇报改动与检查结果
      → 他明确说同意 → 才执行 git push
```

- `pre-push-check.ps1` 没过，不许推；不许"先把明显的修掉，剩下的下次再说"。
- 不许用 `git push --force` 掩盖问题。
- 不许临时关掉闸门（改脚本、加白名单）来换取通过 —— 改闸门本身就是需要单独说明的变更。

---

## 二、隐私红线

### 2.1 禁止入库的内容

仓库内**不得**出现下列任何一项：

| 类别 | 例子（均为示意，非真实值） |
| --- | --- |
| 真实设备地址 | 任何私网 IP 与端口，如 `192.168.x.x:xxxxx`、`10.x.x.x`、`172.16~31.x.x` |
| 设备名 / 昵称 | 手机在蓝牙或系统里显示的个性化名称 |
| 机型与硬件标识 | 具体型号代号、蓝牙 MAC、Wi-Fi BSSID、序列号（SN） |
| 本地账号痕迹 | `C:\Users\<真实用户名>\...` 路径 |
| 网络标识 | 自家 SSID、路由器 MAC |
| 通讯信息 | 手机号、邮箱、联系人姓名 |
| 凭据 | `Bearer` 令牌、`ghp_` / `sk-` 开头的密钥、私钥文件 |
| 真实屏幕内容 | 手机截图、录屏、裸码流（含第三方 App 界面与用户浏览内容） |

### 2.2 示例一律用文档保留地址

| 用途 | 写法 |
| --- | --- |
| 设备地址（需可读的具体值） | `192.0.2.10:5555` —— RFC 5737 文档保留地址，一看就是假值 |
| 设备地址（泛指占位） | `<DEVICE_ADDR>` |
| 设备名 | `My Phone` |
| 机型 | `HarmonyOS NEXT 真机（型号略）` |
| 序列号 | `<SERIAL>` |
| 邮箱 | `user@example.com` |

**判断标准只有一条**：这条信息能不能定位到某一个具体的人，或某一台特定设备？

- 能 → 必须脱敏。
- 不能 → 可以保留。例如协议端口 `8012`、通用 shell uid `2000`、回环地址 `127.0.0.1`、
  开源依赖的包名 `com.huawei.*`（这是上游代码的包名，不是个人信息）。

### 2.3 `samples\` 只允许无隐私的二进制头

- **允许**：`samples/extradata.bin` —— 32 字节 SPS+PPS 参数集，纯协议头，不含任何画面。
- **禁止**：`*.jpeg` / `*.jpg` / `*.png` / `*.gif` / `*.h264` / `*.h265` / `*.mp4` / `*.webm` / `*.mkv`。
  这些是真实手机画面，已在 `.gitignore` 中排除，并被 `pre-push-check.ps1` 第二道闸拦截。
- 确需入库某个媒体文件时：由仓库所有者本人确认该文件已脱敏（无界面、无通知、无个人信息），
  再把文件名写进 `tools/samples-allowlist.txt`。**不要为了通过检查而批量加白。**

### 2.4 源码里不许有默认真机地址

设备地址必须由运行时的 `--device` 参数或用户配置提供。代码中的默认值必须是空字符串，
**不允许**出现任何真实地址或"看起来像真的"默认值。

---

## 三、提交前检查

### 3.1 一条命令跑完三道闸

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\pre-push-check.ps1
```

退出码 `0` 才算通过。三道闸分别是：

| 闸 | 内容 | 失败含义 |
| --- | --- | --- |
| 1 | 隐私扫描 | 仓库里仍有能定位到具体人或设备的字符串 |
| 2 | 真实屏幕内容 | `samples\` 下出现了图片/视频/裸码流 |
| 3 | 敏感文件 | 待提交范围里有 `.env` / `*.pfx` / `*.key` / `config.local.*` |

无论结果如何，脚本最后都会打印：

```
⚠ 本仓库禁止未经用户显式许可执行 git push。人工确认后再推。
```

### 3.2 只跑隐私扫描

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\privacy-scan.ps1
```

输出格式为「文件 → 行号 → 命中内容 → 建议替换值」，退出码 `0` 表示干净、`1` 表示有待处理项。

### 3.3 保守自动替换

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\privacy-scan.ps1 -Fix
```

`-Fix` 只处理**结构确定、替换值唯一**的五类：

- 真实内网地址 → `192.0.2.10:5555`
- 设备名 / 昵称 → `My`
- 机型 → `HarmonyOS NEXT 真机（型号略）`
- MAC 地址 / 蓝牙设备 ID → `XX:XX:XX:XX:XX:XX` / `DEV_XXXXXXXXXXXXXXXX`
- 本地用户目录路径 → `%USERPROFILE%\`

**人名、手机号、邮箱、序列号、令牌一律只报告、不自动改。** 这些需要人判断上下文，
机器改容易把技术文档改坏，也容易漏。

### 3.4 扫描范围

默认排除，不需要额外配置：

| 排除项 | 原因 |
| --- | --- |
| `docs/reference/` | 上游公开源码（GPL/Apache），其中的厂商包名不是个人信息 |
| `obj/` `bin/` `.vs/` | 构建产物与 IDE 状态 |
| `_probe/` | 一次性探针脚本，已被 `.gitignore` 排除 |
| `.git/` | 版本库内部数据 |
| `tools/privacy-scan.ps1`、`tools/pre-push-check.ps1` | 检测工具自身含有检测关键词 |

---

## 四、发现泄漏后的处置

1. **立刻停止推送**，即使已经 `git add`。
2. 用 `privacy-scan.ps1 -Fix` 处理确定项，其余人工改写。
3. 如果泄漏内容**已经进入历史提交**：不要只改工作树。需要重写历史
   （`git filter-repo` 或等价手段），并视为一次需要仓库所有者单独批准的破坏性操作 ——
   推送被重写的历史必须重新获得同意，且要提醒协作者重新克隆。
4. 如果已经推到公开远端：修改文件**不等于**移除泄漏 —— 提交历史、Release 附件、
   fork、以及各镜像站都可能留有副本。按"已公开"处理，并同步轮换涉及的凭据。

---

*本文件由维护者维护。修改「发布纪律」或「隐私红线」两节，属于需要单独说明并获批准的变更。*

### 命名：对外名称一律不带 App 后缀

- 可执行文件、安装包、窗口标题、托盘名称、README 标题、Release 附件名，统一用 HdcMirror，**不得出现 App / APP 后缀**。
- AssemblyName 必须是 HdcMirror，产物为 HdcMirror.exe。
- RootNamespace 可保留工程结构名（内部不可见），但任何用户可见位置都不许带 App。
- 提交前自检：Select-String -Path src\**\*.csproj,src\**\Resources.resx -Pattern 'HdcMirror\.App' 只允许命中 RootNamespace。
## 推送通道（2026-11-11 实测）

本机 `github.com:443` 不通（`git push` 报 `Failed to connect ... port 443` / `Recv failure: Connection was reset`），
但 `api.github.com:443`、`ssh.github.com:443`、`uploads.github.com:443` 都通。

因此发布流程如下，不要直接 `git push`：

1. 本地 `git add` + `git commit`（历史照常记录在本地）；
2. 用 `tools\gh-api-push.ps1` 把 **HEAD 的树**整体上传成远端一次提交：

```powershell
powershell -NoProfile -ExecutionPolicy Bypass -File tools\gh-api-push.ps1 `
  -Repo jiumooo/hdc-mirror -Message "提交说明" -ParentSha <远端当前 main 的 sha>
```

- `-ParentSha` 取 `gh api repos/jiumooo/hdc-mirror/git/ref/heads/main --jq .object.sha`；
- 空仓库第一次必须先 `gh api --method PUT .../contents/README.md` 造一个初始提交，否则建 blob 会 409；
- 脚本用 `git -c core.quotepath=false ls-tree` 取路径，中文文件名不会变成八进制转义；
- `gh release upload` 走 `uploads.github.com`，正常可用。
