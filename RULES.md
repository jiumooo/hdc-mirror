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

---

## 五、版本发布审核（2026-11-12 起生效，覆盖 0.0.5 之后的所有版本）

> 起因：0.0.3 ~ 0.0.7 连续五个版本未经仓库所有者测试与审核就推到了 GitHub。
> 这不是"效率高"，是违反了本文件 1.1 / 1.2 两条。以下流程自此强制执行，不再有例外解释。

### 5.1 两道门禁，缺一不可

| 门 | 谁做 | 通过标准 |
| --- | --- | --- |
| 第一道：安装包自测 | 仓库所有者本人 | 在他自己的机器上装、跑、验证本次改动项，亲口说通过 |
| 第二道：内容审核 | 仓库所有者本人 | 看过变更说明与提交内容，亲口说可以发布 |

- 本地构建全绿、三闸全过、自检 9 项全过，**都不替代这两道门**。
- "上次说过可以发"不覆盖这一次；"你自己看着办""快点"不构成审核。
- 只有仓库所有者当轮原话明确指向本次版本，才算通过。

### 5.2 标准流程（顺序不可换）

```
改代码 → 本地构建 + 自检 + 三闸 → 打安装包（只到 dist\，不提交、不打 tag）
      → 把【安装包绝对路径 + 版本号 + 本次改了什么】报告给仓库所有者
      → 他安装、测试
      → 他说"通过 / 可以发" → git commit + tag + push + gh release
```

- 安装包打好之后**先不要 commit**。提交与发布同属被审核的动作。
- 测试没过的版本：停在本地 `tools\pack\dist\`，可反复覆盖；不进仓库、不打 tag。
- 修完再打一次包、再报一次路径、再等他测。循环到通过。

### 5.3 每次发版必须报告的三样

1. 本地安装包绝对路径，固定为
   `D:\Documents\DSH Github 工作区\hdc-mirror\tools\pack\dist\HdcMirror-win-Setup.exe`
2. 版本号（取自 `src\Directory.Build.props` 的 `<Version>`，唯一来源）
3. 本次改了什么、需要重点测哪一条

发布成功后再补第 4 样：Release 链接。**没有安装包路径的报告视为没交付。**

### 5.4 违反后的处置

- 已推的版本不回滚历史（回滚本身是破坏性操作），但必须在当轮对话里说明哪几个版本未经审核。
- 下一版开始严格执行 5.2；连续遵守才算恢复信任。

### 5.5 唯一例外

仓库所有者当轮原话明确说"这一版不用测，直接发"——才可跳过 5.1，并在 Release 说明里注明"本版未经所有者实测"。

### 5.6 日常修 bug 用热更新，不打安装包（2026-11-12 起，所有者要求）

调试期一律走热更新，**不再每次打包 + 安装**：

```powershell
D:\Documents\DSH Github 工作区\hdc-mirror\tools\hotdeploy.ps1
```

它做四件事：结束 `HdcMirror` 进程与安装目录里的残留 `hdc.exe` → `dotnet build HdcMirror.App -c Debug` →
`robocopy bin\Debug\net8.0-windows → %LOCALAPPDATA%\HdcMirror\current`（不删多余文件、不动 `Update.exe` 与 `packages\`）→ 重新启动。

- 热更新**不改变版本号**，装出来的仍是同一个版本的安装记录；`current\` 里的文件被就地替换。
- 只有所有者说"打包新版本 / 上传 GitHub"时，才跑 `tools\pack\build-release.ps1` 并出安装包（见 5.2）。
- 参数：`-Release`（Release 编译）、`-NoStart`（只部署不启动）、`-NoBuild`（只同步现有 bin）。
- 已知坑：`hdc.exe` 常驻服务会锁住 `tools\libusb_shared.dll`，不先结束就覆盖失败（脚本已处理）；
  进程 `Path` 可能返回 8.3 短名 `HDCMIR~1`，匹配关键字不能用绝对路径前缀。


## 5.7 推送必须由用户当面批准（2026-11-14 事故后强制）

起因：操作者早就说过「上传 GitHub 必须先经我允许」，但助手把「打包上传到仓库」这句话直接当成批准，连续推了 0.2.0、0.2.1 两个提交、两个 tag 和两个 Release。**任何含糊的授权都不算批准。**

规则：

1. 助手的动作止于：本地编译 → 本地打包 → `privacy-scan` / `selftest` / `pre-push-check` → 把 diff、安装包路径、Release 文案交给用户。
2. 只有用户明确说「推」「可以推」「上传吧」之后，才允许执行 `tools\approve-push.ps1`，再 `git push` / `gh release`。
3. `.git\hooks\pre-push` 已装上：没有 `.git\ALLOW_PUSH` 时**物理上推不出去**；批准 30 分钟过期、推送一次即作废。
4. 需要撤销批准：`tools\approve-push.ps1 -Revoke`。
5. 不许用 `--no-verify` 绕过钩子。
## 5.8 版本号只有所有者能改（2026-11-14 事故后强制）

起因：一次安装器改动过程中助手自行把版本从 0.2.1 连续升到 0.2.2 / 0.2.3 / 0.2.4，等于自己制造了三个「对外版本」。

规则：

1. `<Version>` 只反映所有者认可的发布版本，**助手不得自行递增**；所有修改都写在**当前版本**下。
2. 本地反复重打包时，删掉 `tools\pack\dist` 里同版本的 `*.nupkg`（或换 `-OutputDir`）即可，不许改版本号绕过 vpk 的「版本重复」检查。
3. 只有所有者说「升版本」「发 0.2.x」时才改 `<Version>`，并同步 `CHANGELOG.md`。