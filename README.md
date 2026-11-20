# Windows 灵动岛

Windows 上的 C# WPF 桌面应用。默认在屏幕顶部收起，鼠标靠近时向工作区展开。

## macOS 预览版

可从 [macOS 预览发布页](https://github.com/Myjianglai/WindowsIsland/releases/tag/v0.3.3-macos-preview.1) 下载 Apple 芯片和 Intel 安装包。

macOS 工程位于 `src/Island.Mac`，使用 Avalonia，并与 Windows 版共用待办模型、Excel 导入导出和本地歌词解析。预览版可在 Windows 上交叉打包。需要 .NET 10 SDK 和 Python 3.11 或更高版本。

```powershell
python tools/package-macos.py
```

产物位于 `src/Island.Mac/bin/macos`，要求 macOS 14 或更新版本。Apple 芯片选 `osx-arm64` ZIP，Intel 芯片选 `osx-x64` ZIP。两者都包含 .NET 运行时。解压后把 `Island.app` 移到「应用程序」再打开。预览包未签名、未公证，macOS 可能要求在「系统设置 → 隐私与安全性」中确认打开。

预览版包含悬浮岛和菜单栏、AI 会话钩子及 Codex / Claude 额度、待办与按日笔记、Excel、Apple Music / Spotify 控制与本地歌词、文本剪贴板、文件中转、系统统计、设备报告、IMAP 未读邮件，以及浏览器中的小红书。钩子需要在设置中安装，音乐控制需要 macOS 自动化权限。剪贴板记录默认关闭，启用后存储在当前用户私有目录内。邮箱授权码仅在本次运行内使用。

微信、企业微信和 QQ 目前仅支持打开客户端。第三方聊天通知接收、岛上审批、富剪贴板、系统级媒体会话、外设协议级电量、内嵌小红书和自动更新尚未移植。预览包已验证 ZIP 结构和 Mach-O 架构，macOS 启动与权限流程需真机确认。

## 项目目录

```text
src/
  Island.Core/                 已实现，纯 .NET 核心
    Events/
      IslandEvent.cs           事件、事件类型和按钮动作
      IslandEventBus.cs        有界 FIFO 队列
    Providers/
      IIslandProvider.cs       Provider 的运行和取消约定
  Island.App/                  WPF 应用
    UI/                        悬浮窗口、托盘、设置
    Presentation/              三态切换、动画、通知展示调度
    Providers/                 SMTC、本地歌词、文字剪贴板、AI 额度、系统监控、设备电量
    Persistence/               本机设置、当前用户启动项、DPAPI 存储
tools/
  claude-statusline.ps1        Claude Code 官方状态栏输出适配
```

## 构建

需要 .NET 10 SDK。在项目根目录运行以下命令。

```powershell
dotnet run --project src/Island.App/Island.App.csproj
```

鼠标移上小岛即展开，移开约 135 毫秒后开始收起动画，动画约 175 毫秒。按住展开卡片上方的短线可拖动，松手后吸附到顶部、底部、左侧或右侧，也可以拖到另一块屏幕。右键托盘图标可打开设置、播放两条排队的演示通知、重置到主屏顶部或退出。开机自启使用当前用户的 Windows 登录启动项。

### 常规设置

在「设置 → 收起外观」中可选经典黑或奶油皮肤、刘海、胶囊或指示条，以及停靠边、沿边位置和 80%–140% 大小。刘海和胶囊可将配额环、系统读数、专辑封面、律动条、歌名或歌词并排显示。律动条随默认音频输出的峰值变化。OLED 像素偏移默认关闭，启用后每静止收起一分钟沿边偏移 1 个物理像素，幅度最多 ±3 像素。极简模式把收起状态显示为细线。

在「显示器与时钟」中可跟随主屏或绑定一个显示器。固定显示器断开时临时回到主屏。自动避让按逗号分隔的 Windows 进程名匹配前台应用，当它与小岛位置重叠时折叠成细线。概览时钟支持 12／24 小时制。功能页勾选决定是否显示，拖动页名前的手柄调整顺序。「应用常规设置」立即生效并保存。展开后可用顶部图标切换功能页。

刘海和胶囊默认按内容调整宽度。启用时钟后，刘海可显示完整日期及秒数。在「设置 → 收起外观」可拖动排序刘海内容，也可以单独开关音乐、待办提醒和电量。外观改动即时生效并自动保存。待办到点会高亮，并弹出一次通知横幅。在「设置 → 设备电量 → 显示与设备」可预览收起效果，调整电量变色阈值，并分别选择收起时和展开页显示的设备。有本机电池时标为「电脑」。设备断开时保留选择，重连且读到电量后会再次显示。

「设置 → 窗口行为」可关闭收起置顶，展开时仍会显示在最前面。三个形态都可覆盖在任务栏区域，展开时回到工作区。任务栏位置沿实际任务栏边调整；选定屏幕没有可见任务栏时按平常的屏幕边停靠。全屏隐藏和最大化切换指示条均可单独设置。

### 音乐、剪贴板与设备

在「设置 → 数据源」勾选要使用的模块，再点「保存数据源设置」。

- 音乐数据源首次安装时默认开启。刘海或胶囊可展示播放信息，即使关闭了展开面板里的音乐页。播放、暂停、切歌及拖动进度取决于当前播放器上报的能力，系统音量滑块控制默认输出设备。网易云、QQ 音乐、酷狗、汽水音乐等播放器需向 Windows 提供媒体信息。
- 网易云正在播放但小岛没有曲目信息时，在网易云「设置 → 系统」确认「开启 SMTC」已勾选。SMTC 提供曲目、封面和进度，歌词模式会在缺少匹配 LRC 时显示歌名。
- 网易云的 SMTC 时间轴为空时，小岛会从本机 `cloudmusic.elog` 补读当前曲目的进度、暂停与跳转事件。日志原文不保存或发送；能否使用此回退取决于网易云客户端版本及当前播放记录。
- 在「设置 → 显示器与时钟」可启用「跨播放器自动获取歌词（联网）」。QQ 音乐和酷狗优先查询对应的歌词服务，未匹配时使用 LRCLIB 或网易云。其他提供 Windows 媒体会话的播放器可用 LRCLIB 或网易云查词。开启后会向歌词服务发送当前曲名、歌手等匹配所需的曲目信息。网易云客户端优先用歌曲 ID 查词，无需打开桌面歌词，也可以只启用「网易云自动获取歌词（联网）」。两个联网开关默认关闭。
- 本地 LRC 文件匹配优先，支持 `歌名 - 歌手.lrc`、`歌名.lrc` 和网易云 `歌曲ID.lrc`。有带时间轴的歌词且播放器提供有效进度时，音乐页会同步。顶部或底部停靠时可在收起外观中选「当前歌词」显示一行。
- 剪贴板支持 Unicode 文字、HTML／RTF／CSV、PNG／位图截图及文件或文件夹的 HDROP。点击历史条目后可在目标应用按 Ctrl+V。历史记录至多保留 30 条近期内容并受存储上限约束，固定项单独保留。文件历史保存路径，重新复制时按复制处理。常见密钥会掩码预览，带历史排除标记的内容会跳过。历史使用 DPAPI 加密保存在 `%LOCALAPPDATA%\WindowsIsland\clipboard-history.bin`。
- 把文件拖到小岛可加入「置物架」，点击打开，也可定位、拖出副本或移除条目。置物架只在本次运行中保存文件路径，移除条目不会删除原文件。
- 系统监控每 2 秒采样 CPU、物理内存及网卡上下行速率。展开页左侧显示总 CPU、各逻辑处理器、内存、本次监控的累计流量和可用的系统电池读数。右侧可切换 CPU 或内存进程排行。收起的小岛保留精简读数。
- 设备页默认自动刷新，优先读取 Windows 为已连接蓝牙设备提供的电量属性，再尝试 BLE 标准电池服务。Logi Bolt 和部分 Unifying 接收器通过 HID++ 获取设备电量。XInput 无线手柄可显示电量等级。有精确百分比时显示电量环，低电量变色，设备未报告时显示「未知」。
- 待办清单页支持添加、勾选、编辑、删除和通过 Excel（.xlsx）导入导出。导入时合并到现有清单并跳过重复项，自己制作导入表时第一行至少包含「标题」列。点条目旁的时钟可设置提醒时间和每周重复日。小岛运行时通过横幅提醒，记录加密保存在本机，重启后可继续查看。
- 小红书标签页默认关闭，可在「设置 → 展开外观」开启。它通过 WebView2 加载官方网页，浏览数据保存在小岛的独立本机配置中。页面需联网，也可通过「在浏览器打开」访问。

## AI 额度

「AI 会话」是独立的功能页，显示 Codex、Claude Code 的运行状态、会话标题和最近活动。通过「设置 → AI 会话」安装或修复生命周期钩子，重启对应工具后生效。Codex 首次运行时需要确认信任钩子。可选的岛上审批支持允许本次、拒绝或交回终端。

打开「设置 → AI 额度」。每个账号可单独设置名称、配色和固定状态。可固定最多 3 个账号；返回用量百分比的账号显示配额环。常规设置可选择单环或多环、工具首字母、已用或剩余显示。多环时每个账号最多三个同心环，在账号编辑页可单独选择每环对应的额度。余额或积分按原单位显示。额度首次跨过 70% 或 90% 时可提醒。

点击「自动识别本机工具」可添加已发现的 Codex、Claude 或 Cursor。Codex 通过官方 CLI 复用登录。Claude 只使用用户提供的官方状态输出，安装 Claude 桌面版可被检测，只有存在可用状态文件才显示百分比。Cursor 可用官方 CLI 检查登录，个人额度仍以官方用量页面为准。

| 服务 | 接入 |
| --- | --- |
| Codex | 自动识别已安装的原生 exe 或选择路径。通过本地 App Server 查询套餐额度与重置时间，服务返回积分时一并展示。多账号可指定不同的已登录配置目录。 |
| Claude | 使用 Claude Code 的官方状态栏 JSON，读取 5 小时、7 天及支出上限的用量与重置时间。过期或缺失字段显示未知。 |
| WorkBuddy | 可手动录入客户端显示的剩余积分。公开接口提供 OAuth 和积分兑换，个人余额自动识别与浏览器授权查询待有可用的余额读取接口。 |
| DeepSeek | 填写 API Key，直连官方 API 按币种显示总余额、充值金额和赠送金额。可为不同 Key 分别命名。 |
| Cursor | 检测本机安装或官方 CLI 状态。个人额度没有接入。 |

账号配置与 DeepSeek 密钥使用当前 Windows 用户的 DPAPI 加密存放于 `%LOCALAPPDATA%\WindowsIsland\quota-accounts.bin`。Codex 使用官方客户端现有登录状态查询，DeepSeek 定期直连官方接口。Claude 状态文件只含额度字段。应用没有中转服务器。

### Claude Code 状态栏设置

将下面的片段合并到 Claude Code 的用户设置。按实际位置替换脚本路径。已有状态栏时，可在现有状态栏脚本中把同一份输入 JSON 同时送给本脚本。

```json
{
  "statusLine": {
    "type": "command",
    "command": "powershell.exe -NoProfile -ExecutionPolicy RemoteSigned -File \"D:\\MyProject\\tools\\claude-statusline.ps1\""
  }
}
```

在 Claude Code 完成一次请求后，脚本默认会写入 `%LOCALAPPDATA%\WindowsIsland\claude-status.json`，账号设置里的状态文件路径留空即可。多账号可为各自的 Claude Code 环境设置不同的 `-OutputPath`，再在管理页填写对应路径。

## 下载与更新

待办页可在待办、按日期保存的笔记和月历之间切换，Excel 导入导出覆盖全部日期。剪贴板与文件设置支持图片记录、条数、独立窗口、快捷键和文件中转自动清理。

消息页按微信、企业微信、QQ 和邮箱分组。微信与企业微信可接收任务栏闪烁提醒，QQ 消息预览需要 Windows 通知访问权限，邮箱需要配置 IMAP。点击微信或企业微信提醒可打开已安装的客户端。收起态的消息预览保留约 5 秒。

从 [WindowsIsland 发布页](https://github.com/Myjianglai/WindowsIsland/releases) 下载 Windows x64 版 `Island.exe`，双击运行。下载无需 GitHub 账号。

在「设置 → 版本与更新」中检查最新版。自 v0.2.8 起，更新检查使用公开发布的 `update.json`，无需 GitHub 授权。点击「下载更新」可查看进度。小岛校验 SHA-256 后会询问是否安装并重启。也可以稍后在同一设置页点击「安装并重启」。EXE 所在目录需要有写入权限。

发布时更新 `src/Island.App/Island.App.csproj` 的 `Version`，提交并打上对应的 `v主版本.次版本.修订版本` 标签。将生成的 `Island.exe` 和 `update.json` 上传到本仓库的同名 Release。清单包含 `version`（不带 `v`）、`size`（字节数）和 `sha256`（十六进制摘要）。迁移期间保留旧发布仓库，供旧客户端检查更新。

```powershell
dotnet publish src/Island.App/Island.App.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=None -o src/Island.App/bin/releases
```

## 事件交付

- 每个 Provider 通过 `IIslandEventPublisher` 发布事件。配置使用 Provider 的稳定 ID，事件使用相同的 ID 作为 `Source`。
- 队列默认容纳 64 条事件。常规异步发布在队列满时等待空位，可用取消令牌退出。系统回调可以使用 `TryPublish`，在队列无法接收时立即获得失败结果，后续由对应 Provider 决定如何合并事件或重试。
- WPF 展示调度器独占读取队列，并等待当前横幅收起后再读取下一条。超时和动画由调度器与窗口管理。
- 托管层隔离各 Provider 的异常。媒体、剪贴板及额度事件刷新标签页，通知类事件排队展示横幅。

## 后续阶段

1. 用户指定文件夹的监听和 Microsoft To Do。
2. Mica/Acrylic 原生材质。

应用数据在本机处理及保存。邮箱和微软待办需要联网，后续设计为用户启用后直连所配置的 IMAP 服务或 Microsoft Graph。勾选完成会向 Graph 写入该待办的完成状态。

收起形态和网易云日志格式参考 BrimDeck。系统读数旁的闪电图形来自 BrimDeck 的 `PowerIcons.cs`，按 Apache-2.0 使用。[BrimDeck](https://github.com/MoFeng2223/BrimDeck) · [许可证](src/Island.App/Licenses/BrimDeck-Apache-2.0.txt)
