# 字幕宝 · Windows 版

[![Windows](https://github.com/yangli-0409/Awesome-Realtime-Translator/actions/workflows/windows.yml/badge.svg)](https://github.com/yangli-0409/Awesome-Realtime-Translator/actions/workflows/windows.yml)

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

1. 下载解压。**整个文件夹要保持原样**——`models`、`runtimes` 和那几个 dll
   都得和 exe 在一起，不能只把 exe 拖出来。要快捷方式就右键 exe → 发送到 → 桌面。
2. 双击 `AwesomeRealtimeTranslator.exe`。
3. 字幕窗出现在屏幕顶部，点右上角 ▶ 开始听。

没有安装程序，没有权限弹窗，不用重启。

没有买代码签名证书，所以 Windows 会弹「未知发布者」，极少数杀毒软件也可能误报。
这是这个分发方式的固有属性，不是 bug，只能靠签名彻底解决（见下）。

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
cd windows && ./tools/fetch-models.sh && dotnet test tests/Core.Tests/Core.Tests.csproj
```

```bash
pwsh tools/package.ps1
```

**整个解决方案在 macOS 和 Linux 上也能构建**（`EnableWindowsTargeting`），
这个移植就是在一台 Mac 上写完的。但**测试分两半**，别在非 Windows 上
直接跑 `dotnet test`（那会连 `App.Tests` 一起拉进来，必然失败）：

```
windows/
├── src/Core/          平台无关：断句、显现节奏、管道装配、翻译
├── src/App/           Windows 专有：WASAPI、Whisper、WPF 界面、Win32 互操作
├── tests/Core.Tests/  87 个单测，任何平台都能跑
├── tests/App.Tests/   23 个，界面 / Win32 / 真截屏，只有真 Windows 能跑
└── tools/             取模型、画图标、打包
```

分层的理由是**可验证性**：凡是能放进 Core 的逻辑都放了进去，于是它们在开发机上
就能真跑测试。留在 App 里的只有非 Windows 不可的部分。

### CI 替开发机跑 Windows

[`.github/workflows/windows.yml`](../.github/workflows/windows.yml) 在
`windows-latest` 上还原、构建、跑两个测试工程，另一个 job 构建 `win-arm64`。

**这是 0.1.5 才有的，理由就是上面那个 bug。**「能编译」被当成「能跑」
整整四个版本，代价是一个真实用户在面试前踩雷。

0.1.5 时 `App.Tests` 做的事少到有点可笑——把每个 XAML 窗口 `new` 一次。
但那个 bug 就在构造函数里，所以这一下就够了（构造窗口不创建 HWND，
CI runner 上照样跑）。

**0.2.0 把它扩成了真正的验证层**，因为想清楚了一件事：既然 runner 上有一个
真的 Windows 桌面，那就不只是能 `new` 窗口，而是能 `Show()`、能读窗口样式、
**能截屏**。于是这个项目最大的那个未知项，第一次有了自动化的答案。
四个文件，23 个用例：

| 文件 | 守什么 |
|---|---|
| `ScreenCaptureTests` | 真的截一次屏，被排除的窗口必须不在图里；关掉排除必须又出现 |
| `Win32BehaviourTests` | `OnSourceInitialized` 里那几个窗口标志真的设上了 |
| `SettingsBehaviourTests` | 设置窗的回调真的改到设置、真的落盘 |
| `WindowConstructionTests` | 每个 XAML 窗口都必须能被 `new` 出来 |

新加窗口就往 `WindowConstructionTests` 里加一个用例；新碰 Win32 就往
`Win32BehaviourTests` 里加一个。

**这些测试被验证过是真的有牙**，不是写上去好看的。做法是把 0.1.5 的修复
回退到一个临时分支上、`workflow_dispatch` 跑一次：

| | 结果 |
|---|---|
| 四个 `SettingsWindow` 用例 | **全红**，堆栈就是线上那条：`NullReferenceException → UpdateLabels → OnFontSizeChanged → InitializeComponent` |
| `CaptionWindow` 用例 | 照常通过 —— 说明它不是无差别地红 |
| 耗时 | 约 3 秒 |

一个花了四个版本、一个真实用户、两轮错误诊断才抓到的 bug，在 CI 上三秒复现。
一个测试值不值，看的就是这个。

#### 一个关于测试环境的教训

0.2.0 第一版的 `Win32BehaviourTests` 在 CI 上四个全红：

```
InvalidOperationException: Cannot use a DependencyObject that belongs to
a different thread than its parent Freezable.
    at System.Windows.Freezable.EnsureConsistentDispatchers(...)
```

原因是当时每个用例都开一条新的 STA 线程，而 WPF 会缓存 BAML 里可共享的
Freezable（画刷、样式），缓存项带着「第一个创建它的线程」的亲和性。
于是第一个跑到的用例成功，后面的全挂。

**这是测试写法的错，不是产品的错**——真实应用只有一条 UI 线程，永远碰不到。
但它值得记下来：把测试环境搭得和真实运行环境不一样，测出来的失败就可能
既不是真 bug、也不能证明没有真 bug。改成全进程一条 STA 线程 + 一个真正的
`Dispatcher` 消息循环之后，五个用例全过——也就是说 Win32 那部分接线一直是对的。

## 四件和 Mac 版实现思路不同的事

### 1. Whisper 不是流式的，流式语义是自己造的

macOS 的 SpeechAnalyzer 本身就吐 volatile / finalized 事件。Whisper 一次吃一整段音频，
所以 `WhisperTranscriber` 用「滑动窗口 + 重复解码」造出同样的语义：

- 每 700ms 把**整个当前窗口**重新解码一次 → 作为 volatile 发出去;
- 检测到停顿超过 600ms → 再解码一次 → 作为 finalized 发出去,清空窗口;
- 解码结果里出现**句末标点**且前缀连续两次解码没变 → 把前缀定稿,窗口裁到那之后;
- 窗口超过 7 秒强制定稿,否则解码耗时会随窗口线性增长。

代价是同一段音频被反复解码好几遍,用 CPU 换「边说边出字」。

第三条是 0.3.0 加的,起因见下面的「连续说话时译文停摆」。定稿的全部判断抽在
[`FinalizePolicy`](src/Core/FinalizePolicy.cs) 里——纯逻辑、12 个单测,
能在没有 Windows 的开发机上跑。这是这个项目反复用的同一招:
**凡是说不清对错的判断,就抽成不依赖平台的纯函数,否则只能靠猜。**

它只回答「哪段音频可以安全退休」,不回答「句子在哪里结束」。遇到 `Dr.` 这种缩写
它会误判成边界,没关系:定稿出去的文本以 `Dr.` 结尾,`Segmenter` 的缩写表拒绝在
那里切句,它留在 buffer 里等下一段拼上。职责分开之后,误判的代价只是多留几秒音频。

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

### 4. 原生库不打进 exe（0.1.2 改掉的）

Mac 版是一个 `.app` 包，看起来是一个图标，其实是目录。Windows 这边 0.1.0/0.1.1
想模仿这种「一个文件」的观感，开了 `IncludeNativeLibrariesForSelfExtract=true`,
把 onnxruntime、WPF 的原生库、以及 `runtimes/win-*/` 下的 whisper 库全部塞进 exe。

这个选择是错的，两头都不成立：

**好处本来就不存在。** 语音模型 148MB、翻译模型 240MB，塞不进 exe，`models/`
必须和 exe 同目录。用户从头到尾拿到的都是一个文件夹，「单文件」只是自我安慰。

**代价是真的。** 开了这个开关的 exe 每次启动都要把原生库解压到
`%TEMP%\.net\` 再加载。「进程把可执行代码写进临时目录然后加载它」是杀毒软件
启发式规则里的典型 dropper 行为。更要紧的是 Whisper.net 的加载器是**按路径**
找库的（`runtimes/<rid>/whisper.dll`，见它自己的 `.targets`），而 0.1.1 的 exe
旁边根本没有这个目录。

对照构建把差别摊开了：

| | 0.1.1（自解压） | 0.1.2（现在） |
|---|---|---|
| exe | 171 MB | 147 MB |
| exe 旁边的原生库 | 无 | 6 个 dll + `runtimes/win-*/` 共 12 个 |
| 启动时往磁盘写东西 | 有 | **没有** |

0.1.2 只留托管程序集在 exe 里（从内存加载，不落盘），原生库放回 Whisper.net
和 .NET SDK 本来期望的位置。代价是用户不能只把 exe 拖走——写进了使用说明。

## 已经验证的 / 还没验证的

**这个移植是在一台 Mac 上开发的，没有 Windows 机器。** 下面这条分界线因此特别重要。

**2026-11-24 更新：有用户在真 Windows 上跑起来了。** 下面的分界线按这次反馈重画过。
同一位用户的第二次反馈（录屏）又揪出了「连续说话时译文停摆八秒」，0.3.0 修掉了。

**已验证（CI，windows-latest，每次推送）**

0.2.0 起这一栏是机器在守的，不是我记得去跑的。0.3.0 加到 142 个用例
（Core 115 + 真 Windows 上的界面与 Win32 27），全过。

*产品最关键的那条承诺*

- **真的截一次屏，确认被排除的窗口不在截图里。**0.2.0 之前这件事从来没有
  任何人、任何机器验证过——代码里只有一次 `GetWindowDisplayAffinity` 回读，
  而「回读说设上了」和「截图里真的没有」之间差着一整个未知。现在
  `Graphics.CopyFromScreen`（底下就是 BitBlt）截主屏，数像素。
  **对照组同时跑**：先不排除截一次，必须找得到测试窗，找不到就说明这台
  runner 截不到图、本用例不作数——不能让一张纯黑截图假装成「隐藏成功」。
- 关掉排除之后窗口必须重新出现。用户关掉那个开关，是期望它真的关掉的。
- 字幕窗自己有没有把标志设上：开关打开 → `WDA_EXCLUDEFROMCAPTURE`，
  关掉 → `WDA_NONE`。

*其余 Win32 行为，全都发生在 `OnSourceInitialized` 里，构造测试碰不到*

- `WS_EX_NOACTIVATE` + `WS_EX_TOOLWINDOW`：不抢键盘焦点、不进任务栏。
- 鼠标穿透开关两个方向都验：`WS_EX_TRANSPARENT` 设得上、也撤得掉
  （撤不掉的话用户再也点不到字幕窗）。
- 全局快捷键的 modifier 和虚拟键码是合法的。传错了会和「被别的程序占用」
  长得一模一样，永远查不出来。

*界面*

- 两个 XAML 窗口都能构造出来，标签和滑块真的被填上了。
  就是这一条在 0.1.0–0.1.4 之间漏掉了（见下）。
- 设置窗那些回调真的改到设置了。它们从 0.1.0 到 0.1.5 **一次都没被执行过**，
  因为设置窗根本打不开。
- 延迟落盘真的会落（停手 300ms 之后），`Dispose` 真的兜得住最后一下改动。
- 托盘图标来自嵌入资源，不是系统默认图标。
- 版本号在设置窗上看得见。
- **译文控件真的是 `StreamingTextBlock`，`Pace` 真的绑上了。**0.3.0 加的，
  走一遍可视化树确认——因为上一版就是「以为接上了」才让用户白调了半天滑块。
  改设置之后 `Pace` 要跟着变，这条也验。

*定稿节奏（0.3.0 加的，全部在 Mac 上也能跑）*

- 连续说话、没有静音、窗口没到上限时，**必须**有文本定稿出来。
  用例里的台词取自用户录像，不是编的。
- 候选边界后面没有下一段时不定稿（末段还在生成，尾巴最不可靠）。
- whisper 回头改写前缀时，稳定计数归零重来——定稿不可撤回。
- `SilenceFlushInterval` 必须大于识别器发 volatile 的间隔。这条耦合直接
  写成了断言，免得以后又有人去调小它。
- 流式译文的中间前缀严格 append-only，哨兵写到一半不会露在屏幕上。

*构建*

- `win-x64` 和 `win-arm64` 都零警告构建通过。
- Core.Tests 115 个**全跑，0 跳过**：CI 会缓存并下载真实的 OPUS-MT 模型，
  所以翻译那 5 条不再被 `Skip` 掉。0.1.5 时它们在 CI 上是跳过的。

**已验证（开发机上）**

- 整个解决方案零警告编译，产物是真正的 Windows PE32+ 可执行文件。
- 上面那 115 个单测覆盖断句（27 条从 Swift 逐条移植，行为完全一致）、
  显现节奏、定稿节奏、整条管道的装配与降级路径、术语保护、流式译文的有序性。
- **翻译是真跑过的**，不是只编译通过：ONNX Runtime 跨平台，所以用真实模型在
  开发机上完成了端到端翻译，上面那些译文就是实际输出。单句 36~128ms。
- 管道的全部错误路径：识别器加载失败、翻译器降级、音频中断、停机冲刷。

**已验证（真实 Windows 用户，0.1.3 / 0.1.4 的日志）**

- **WASAPI loopback 真的抓到了声音。** 原来的第一大未知项，通过。
- Whisper.net 在 0.1.2 之后的目录布局下能找到原生库，模型加载正常。
- 整条链路出字幕、出译文。
- 反过来也验证出两件坏事，都有堆栈：设置窗从 0.1.0 起就没打开过，
  以及程序放在非系统盘时模型加载会崩。两条都在 0.1.5 里处理了（见下）。

**还没验证**

1. **会议软件自己的采集方式。**0.2.0 验到的是 Windows 自带的那条路
   （GDI / BitBlt）。但 Zoom、Teams、飞书的桌面客户端抓画面的方式不止一种
   ——Desktop Duplication、Windows Graphics Capture、各家自己的驱动。
   `WDA_EXCLUDEFROMCAPTURE` 在文档上覆盖前两者，但**没有人在真的会议软件里
   实测过**。所以「共享前自己用共享预览确认一次」这句不撤，使用说明里也留着。
   这是现在排第一的未知项，但它比 0.1.5 时那个「完全没人验过」小了一整个量级。
2. 设置窗**点开之后是不是真的出现在你眼前**。构造、回调、落盘现在都验了，
   但「点 ⚙ 它会不会开在别的窗口后面」取决于 `Topmost` 和前台切换限制
   那段推理，而**那段推理仍然没被实测过**——没法自动化，得有人坐在 Windows 前面点一下。
3. Whisper 在 CPU 上的实际解码速度，以及端到端延迟到底是多少。
   700ms 的步长是按「base 模型解码 5 秒窗口约 200~400ms」估的，没实测过。
   CI 上不验这条：runner 的 CPU 和真实机器没有可比性。
   0.3.0 新加的两个阈值同样属于这一栏：`StableDecodesToCommit = 2`（约 1.4 秒
   才定稿）和 `MaxWindowSeconds = 7`，都是按用户那段录像的节奏推的。
   **逻辑验过了，取值没有。**真机上如果发现 whisper 会回改已经定稿的前缀，
   就得把 2 往上调；如果译文还是出得慢，就往下调。
4. 真人语速下的识别准确率。
5. 长会话的稳定性、内存和 CPU 占用。
6. 全局快捷键在真实机器上会不会被别的程序占用（参数合法性 CI 已经验了，
   「有没有被占」取决于那台机器上装了什么）。
7. 在装了第三方杀毒软件的机器上会不会被误报拦截。
8. 放在非系统盘上模型加载崩掉那件事，病因还没查清（见下）。

第 1 条和第 2 条都需要一个活人坐在 Windows 前面，自动化到不了。
其余是调参和环境问题。

### 设置窗从来没有打开过（0.1.5 修的）

第一次真实 Windows 反馈同时带来了一个 bug：**点「设置」卡死或闪退。**
0.1.4 猜了两个病因，**都猜错了**。真正的原因是 0.1.5 才定位到的，
而定位它靠的是 0.1.4 里唯一那个不是猜测的改动。这一节按时间顺序记，
因为过程本身比结论更值得留着。

**真正的病因：XAML 解析期间的 NullReferenceException**

```
NullReferenceException at SettingsWindow.UpdateLabels()
  ← SettingsWindow.OnFontSizeChanged()
  ← Slider.OnValueChanged()
  ← RangeBase.OnMinimumChanged()          ← 注意这里
  ← XamlObjectWriter.WriteEndMember()
  ← SettingsWindow.InitializeComponent()  ← 窗口还在构造中
```

XAML 里写的是 `<Slider x:Name="FontSizeSlider" Minimum="11" …>`，而 `Slider.Value`
的默认值是 0。解析器设 `Minimum=11` 的那一刻，WPF 把 Value 从 0 强制抬到 11，
**当场**触发 `ValueChanged` → `OnFontSizeChanged` → `UpdateLabels()`。
可此刻 `InitializeComponent()` 还在往下走，`FontSizeValue` 这个 TextBlock
在 XAML 里写在 Slider **后面**，字段还是 null。

四个滑块的 `Minimum` 都大于 0，所以四个都会这样触发。这不是偶发、不挑机器、
不挑时序：**从 0.1.0 到 0.1.4，设置窗在任何一台 Windows 机器上都没有真正打开过。**

原来的 `_loading` 守卫只挡住了「写配置」这一半：

```csharp
if (!_loading) { _settings.FontSize = e.NewValue; }
UpdateLabels();   // ← 没挡住，就漏在这里
```

修法是把守卫下沉到 `UpdateLabels()` 自己身上——一处生效，四个处理器全覆盖，
以后新加的处理器也不会再踩同一个坑。

**0.1.4 猜错的两条**

一是 `WS_EX_NOACTIVATE` 导致 `Activate()` 被前台切换限制拒绝、设置窗开在别人
后面。这个机制是真的、有文档的，但窗口压根没构造出来，走不到那一步。
代码保留了（代价为零，拿到焦点就自动撤掉 Topmost），但**到底需不需要它，
仍然没有被实测验证过**，注释里写明了。

二是滑块每跨一个刻度就同步落盘导致卡顿。`FileSettingsStore` 以前确实是
「写临时文件 + `File.Move` 替换」，一条不透明度滑块从 0.3 拖到 1.0 会触发
35 次，叠在 UI 线程上确实不合理。改成了停手 300ms 合并写一次、退出时
`Flush()` 兜底。这个改动本身是对的，但它也不是用户报的那个 bug——
用户连滑块都没见到过。

**0.1.4 真正起作用的那一条**

`AppDomain.UnhandledException` 和 `TaskScheduler.UnobservedTaskException` 都
接不到 WPF Dispatcher 上的异常，所以任何界面异常都会直接把进程带走，
用户看到的就是「闪退」，还什么说法都没有。0.1.4 挂上了
`DispatcherUnhandledException`，又在 `OpenSettings()` 外面单独包了一层
（托盘菜单的回调跑在 WinForms 的嵌套消息循环里，不一定经过 Dispatcher）：
记日志、弹个能看的提示、标成已处理。

**就是这一条换来了上面那段堆栈。** 教训很直白：在一个自己跑不起来的平台上，
与其对着症状猜病因，不如先保证下一次失败会带着证据回来。

### 放在 E 盘时模型加载直接崩（0.1.5 加了提示，病因没查清）

同一份日志里还有一条，和设置窗无关：

```
TranscriberException: 语音识别模型加载失败：External component has thrown an exception.
SEHException: External component has thrown an exception.
   at WhisperTranscriber.PrepareAsync(…)
```

`SEHException` 是原生层踩空了。日志里的证据很干净：

| 程序目录 | 启动次数 | 模型加载 |
|---|---|---|
| `E:\实时翻译\…` | 2 | **2 次全失败** |
| `C:\Users\…\Downloads\…` | 3 | 3 次全正常 |

文件大小检查是过了的（日志记的是 141 MB，正常值），所以不是下载到一半那种
明显的半截文件。做了两件事：

1. **加了文件头校验。** ggml 文件前 4 字节是 `GGML_FILE_MAGIC`（`0x67676d6c`，
   落到磁盘上是 ASCII `lmgg`，对着真实的 `ggml-base.bin` 用 xxd 核过）。
   大小对得上、内容是坏的这种情况确实存在，先验这一下就能给出一句人话，
   而不是把一个 `SEHException` 丢给用户。这也很可能就是 E 盘那次的真因。
2. **单独 catch 了 `SEHException`**，把上面那条经验直接写进错误提示：
   把整个文件夹复制到系统盘下再跑。

**病因没查清**，也没有 Windows 机器可以复现。所以提示的措辞是「已知有效的办法」，
不是「原因是」——这个区别在这个项目里已经吃过两次教训了。

### 托盘图标一直是系统默认图标（0.2.0 修的）

`LoadTrayIcon()` 去 `AppContext.BaseDirectory/Resources/app.ico` 找图标，
但 `<ApplicationIcon>` 只把图标嵌进 exe 的 PE 资源，**不会往输出目录拷这个文件**。
于是 `File.Exists` 永远是 false，从 0.1.0 到 0.1.5 托盘一直显示 Windows 通用图标。

修法是把 ico 同时作为 `EmbeddedResource` 打进托管程序集，从
`GetManifestResourceStream` 读。两个细节：

- **按名字后缀找资源**，不写死 `AwesomeRealtimeTranslator.Resources.app.ico`。
  写死的话 `RootNamespace` 一改就又悄悄退回通用图标——正是原来那个 bug 的形状。
- 指定 `SystemInformation.SmallIconSize`。ico 里有 16 到 256 七种尺寸，
  不挑的话托盘会拿一张大图再缩，边缘发虚。

CI 里有用例盯着：嵌入资源必须存在，且 `LoadTrayIcon()` 返回的尺寸必须是小图标
（兜底的 `SystemIcons.Application` 是 32×32，一眼能分出来）。

### 另外两处 0.2.0 顺手修的

**版本号用户看不到。**以前只在日志里。这个项目两天发了四个版本，每次排查第一句
话都是「你装的是哪一版」，而用户得去翻 `%APPDATA%` 才能回答。现在设置窗底部和
托盘悬停都显示，值来自 `App.csproj` 的 `<Version>`（`AppVersion.Current`），
不手写、不可能和实际产物对不上。

**`FileSettingsStore` 的 getter 没加锁。**0.1.4 给这个类加了延迟落盘的 `Timer`，
于是 `_root` 开始会被线程池回调碰到（`Flush` → `Save` → `ToJsonString`），
而 `JsonObject` 不是线程安全的。实际撞不上——`AppSettings` 只在构造时读一次，
那时还没人 `Set` 过。但那是「恰好安全」不是「设计安全」，而加锁的代价是零。

### 连续说话时译文停摆（0.3.0 修的）

用户在真会议里录了一段屏发回来，说「译文不流畅，调了显示节奏也没用」。
按 0.5 秒拆帧之后看到的是这个：

| 时间 | 译文最后一句 | 原文待定稿尾巴 |
|---|---|---|
| 0.0s | 所以我没有再讨论这个, 太多的上次,我 | `can share this information... on the backe` |
| 6.0s | 同上，一个字没动 | 涨到三行，`...how leading brands, communities are` |
| 8.0s | 同上，一个字没动 | `...how leading brands, just other.` |
| 10.0s | 一次蹦出**两句** | 新的尾巴 |

**不是不够顺滑，是停滞八秒然后爆发。**四个环节，每个单看都合理：

1. 识别器只在静音超过 600ms 或窗口到 18 秒时才发 finalized。真实会议里对方连着讲，
   没有 600ms 空档，于是唯一生效的触发器就剩 18 秒上限。
2. `Segmenter` 只把 finalized 放进 buffer，volatile 只设 `VolatileTail` 就返回。
   屏幕上那个 `...on the backend. But we really looked at...` 里的句号就在那儿，
   但没有任何人去看它。
3. 于是连续说话时，**翻译队列全程是空的**。
4. 译文控件还是个普通 `TextBlock`，`RevealPace` 只绑在原文尾巴上——
   所以用户说「调了没用」是对的，那根线物理上就没接。

**同一个根还解释了译文乱码。**`So I didn't go into this too, too much last time and I`
被切在 `and I` 上：没有句末标点，54 个字符，离 140 上限差得远。
原因是 `SilenceFlushInterval` 当时是 **0.45 秒，而识别器每 700ms 才发一个 volatile**。
Segmenter 的静音判定看的是「距离上一个事件多久」，所以它测的根本不是对方停顿，
而是识别器的解码节拍——每两个 volatile 之间都会触发一次定稿。
OPUS-MT 拿到一个吊在 `and I` 上的残片，只能吐出语序错乱、末尾挂个孤零零「我」的东西。

四处都改了：

| | 改动 | 代价 |
|---|---|---|
| 1 | 加标点定稿触发器（`FinalizePolicy`） | 无。encoder 已看全句 |
| 2 | `SilenceFlushInterval` 0.45 → 1.4（> StepInterval 0.7） | 无。真正的句末由识别器的 600ms 判，它先触发 |
| 3 | `MaxWindowSeconds` 18 → 7 | 跨窗口的长句会被劈开。明知的取舍 |
| 4 | 译文接上 `StreamingTextBlock`，解码器逐 token 吐前缀 | 无 |

第 4 条值得单说：**流式译文是免费的。**OPUS-MT 的解码器本来就逐 token 产出中文，
旧代码把这些 token 攒到 `</s>` 才一次性返回。encoder 在解码开始前已经看完整个英文
句子，所以提前显示前缀，译文和原来**一字不差**。而且贪心解码是 append-only 的，
不违反「译文一旦出现就不再改写」。真要付质量代价的是切子句再分别翻（英译中的语序
重排会被破坏），那个没有做。

一个顺带修的隐患：`Progress<T>` 是异步派发的，最后一个前缀有可能排在完整译文
后面送达，把整句覆盖成半句。换成了同步的 `SyncProgress<T>`。

**这次没有在没有 Windows 的情况下靠猜。**新增 32 个用例，其中 28 个
（`FinalizePolicyTests`、`SilenceCouplingTests`、`StreamingTranslationTests`）
在 Mac 上就能跑，剩下 4 个（`TranslationPacingTests`）在 CI 的真 Windows 上
把窗口显示出来、走可视化树，确认译文控件确实是 `StreamingTextBlock`
且 `Pace` 真的绑上了——纯粹因为上一次就是「以为接上了」才出的事。

还没验证的：这些阈值在真人语速下合不合适。`StableDecodesToCommit = 2`
（约 1.4 秒）和 `MaxWindowSeconds = 7` 都是按录像里的节奏推的，没有真机数据。

## 许可证

[MIT](../LICENSE)，和主项目一致。
