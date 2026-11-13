# 面试字幕 · Windows 版

macOS 版的 Windows 移植。同一个产品，同一条边界：**只帮你听懂，不替你回答。**

- 上级目录：[项目主 README](../README.zh-CN.md)
- 工程笔记：[macOS 版的实现记录](../docs/ENGINEERING.zh-CN.md)

---

## 一句话说清和 Mac 版的差别

Mac 版 0.8MB，因为识别和翻译都是 macOS 26 自带的系统能力。
Windows 没有这两样东西，所以得自己背着模型走，**安装包约 540MB**。

功能、交互、设计取舍全部对齐；差的是下面这张表。

| | macOS 版 | Windows 版 |
|---|---|---|
| 系统音频采集 | ScreenCaptureKit | WASAPI loopback |
| 需要的权限 | 屏幕与系统音频录制（每次重装都要重授权） | **不需要任何权限** |
| 语音识别 | 系统的 SpeechAnalyzer | 自带 whisper.cpp（142MB） |
| 翻译 | 系统的 Translation 框架 | 自带 OPUS-MT ONNX（240MB） |
| 对屏幕共享隐藏 | `NSWindow.sharingType = .none` | `SetWindowDisplayAffinity` |
| 不抢键盘焦点 | `canBecomeKey = false` | `WS_EX_NOACTIVATE` |
| 不进任务栏 / Dock | `LSUIElement` | `WS_EX_TOOLWINDOW` |
| 安装包 | 0.8 MB | 约 540 MB |
| 术语翻译质量 | 好（Apple 的模型） | 一般，靠内置术语表补救（见下） |

Windows 版**反而有一个地方更好**：不需要任何系统权限。没有 TCC 那一套，
也就没有 Mac 版那个「每次重新编译都要重新授权一次」的老大难。

## 装和用

需要 **Windows 10 2004（19041）及以上**。

1. 下载解压，`models` 文件夹和 exe 必须放在一起。
2. 双击 `AwesomeRealtimeTranslator.exe`。
3. 字幕窗出现在屏幕顶部，点右上角 ▶ 开始听。

没有安装程序，没有权限弹窗，不用重启。

| | 位置 | 快捷键 |
|---|---|---|
| 开始 / 暂停 | 字幕窗右上角 ▶ | `Ctrl+Alt+T` |
| 显示 / 隐藏字幕窗 | 托盘菜单 | `Ctrl+Alt+H` |
| 设置 | 字幕窗右上角 ⚙ | — |
| 退出 | 字幕窗右上角 ⏻ | — |
| 复制 / 导出记录 | 托盘右键菜单 | — |

退出按钮必须在字幕窗上，和 Mac 版同样的理由：这个窗口不进任务栏、不接受键盘焦点，
`Alt+F4` 到不了它。没有这个按钮，用户只能去任务管理器结束进程。

## 从源码构建

```bash
dotnet tool  # 需要 .NET 10 SDK
```

```bash
cd windows && ./tools/fetch-models.sh && dotnet test
```

```bash
pwsh tools/package.ps1
```

**整个解决方案在 macOS 和 Linux 上也能构建**（`EnableWindowsTargeting`），
这个移植就是在一台 Mac 上写完的。`dotnet test` 在任何平台上都能跑。

```
windows/
├── src/Core/      平台无关：断句、显现节奏、管道装配、翻译
├── src/App/       Windows 专有：WASAPI、Whisper、WPF 界面、Win32 互操作
├── tests/         83 个单测，任何平台都能跑
└── tools/         取模型、画图标、打包
```

分层的理由是**可验证性**：凡是能放进 Core 的逻辑都放了进去，于是它们在开发机上
就能真跑测试。留在 App 里的只有非 Windows 不可的部分。

## 三件和 Mac 版实现思路不同的事

### 1. Whisper 不是流式的，流式语义是自己造的

macOS 的 SpeechAnalyzer 本身就吐 volatile / finalized 事件。Whisper 一次吃一整段音频，
所以 `WhisperTranscriber` 用「滑动窗口 + 重复解码」造出同样的语义：

- 每 700ms 把**整个当前窗口**重新解码一次 → 作为 volatile 发出去；
- 检测到停顿超过 600ms → 再解码一次 → 作为 finalized 发出去，清空窗口；
- 窗口超过 18 秒强制定稿，否则解码耗时会随窗口线性增长。

代价是同一段音频被反复解码好几遍，用 CPU 换「边说边出字」。

静音判定按**挂钟时间**算，不靠数静音采样点——WASAPI loopback 在系统完全没声音时
根本不给缓冲区，数采样点会永远数不满。同样的原因，采集层会播一路静音把音频会话钉住，
否则对方开口前的头几百毫秒会被吞掉。

### 2. 翻译要自己跑推理，技术词汇需要额外保护

OPUS-MT 是通用语料训练的，在软件面试这个场景里会把关键词翻错。实测：

```
architecture            → 建筑          （应为「架构」）
container orchestration → 集装箱的管弦   （应为「容器编排」）
```

试过两种修法：

1. 把中文术语直接塞进英文原句 → 模型产出乱码（`49/ 18 / 12 / 12 /`），废案。
2. 换成 `ZQX7` 这样的哨兵，翻完再换回中文 → 模型原样复制，位置也对。

所以走第二种，见 `TermProtector`。内置术语表只收**实测翻错**的词，不是凭印象堆的。
修好之后：

```
We use Kubernetes for container orchestration at scale.
→ 我们使用 Kubernetes 来计算容器编排的缩放。

We had a race condition that caused a deadlock in the message queue.
→ 我们有一个竞态条件, 导致消息队列的死锁。
```

语法偶尔别扭，但意思准确——这正是产品要的（只帮你听懂）。

### 3. 应用自己永远不联网

Mac 版「一次网络请求都没有」是靠系统自带模型做到的。Windows 要自带模型文件，
但**自带不等于运行时去下载**：一旦应用自己发请求，那条承诺就破了，而且用户没法
验证它只下了模型。

所以模型随安装包一起发，应用只在本地找；找不到就明确报错，绝不偷偷联网。
唯一的下载动作是用户自己在浏览器里下安装包。

## 已经验证的 / 还没验证的

**这个移植是在一台 Mac 上开发的，没有 Windows 机器。** 下面这条分界线因此特别重要。

**已验证（在开发机上真跑过）**

- 整个解决方案零警告编译，产物是真正的 Windows PE32+ 可执行文件。
- 83 个单元测试全过，覆盖断句（27 条从 Swift 逐条移植，行为完全一致）、
  显现节奏、整条管道的装配与降级路径、术语保护。
- **翻译是真跑过的**，不是只编译通过：ONNX Runtime 跨平台，所以用真实模型在
  开发机上完成了端到端翻译，上面那些译文就是实际输出。单句 36~128ms。
- 管道的全部错误路径：识别器加载失败、翻译器降级、音频中断、停机冲刷。

**没验证（需要一台 Windows 机器）**

1. **WASAPI loopback 到底有没有抓到声音。** 最大的未知项。
2. **`SetWindowDisplayAffinity` 到底有没有让字幕窗在共享里消失。** 第二大未知项，
   而且是这个产品最关键的承诺——代码里做了回读确认，失败会明确提示用户，
   但「回读说成功」和「Zoom 里真的看不到」之间还差一次实测。
3. Whisper 在 CPU 上的实际解码速度，以及端到端延迟到底是多少。
   700ms 的步长是按「base 模型解码 5 秒窗口约 200~400ms」估的，没实测过。
4. 全局快捷键是否被别的程序占用。
5. 托盘图标、单文件自解压、首次启动耗时。
6. 长会话的稳定性、内存和 CPU 占用。

把第 1、2 条跑通，这个版本才算真的能用。其余都是调参。

## 许可证

[MIT](../LICENSE)，和主项目一致。
