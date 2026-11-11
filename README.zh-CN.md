# Awesome-Realtime-Translator

**官网与下载**：<https://yangli-0409.github.io/Awesome-Realtime-Translator/> ·
[English README](README.md)

面试实时字幕翻译（macOS）。抓取电脑正在播放的声音，在悬浮窗里显示原文字幕和中文翻译。
全程本机处理，沙盒层面无网络权限，不加入会议，对方无感知。

对应 PRD：`M1 原型`（F1–F6）+ 顺手做掉的 F7/F8/F9。

---

## 一、构建与运行

```bash
cd Awesome-Realtime-Translator && xcodegen generate && open AwesomeRealtimeTranslator.xcodeproj
```

命令行构建 + 跑测试：

```bash
xcodebuild -project AwesomeRealtimeTranslator.xcodeproj -scheme AwesomeRealtimeTranslator -destination 'platform=macOS' test
```

工程文件由 `project.yml`（XcodeGen）生成，`.xcodeproj` 也一并提交，没装 xcodegen 也能直接打开。
改了文件结构只需重新 `xcodegen generate`，不会产生 pbxproj 冲突。

**环境要求**：macOS 26.0+、Xcode 26+、Apple Silicon。为什么不是 PRD 里写的 macOS 15，见第三节。

## 二、首次运行授权（F2）

1. 启动后应用只在菜单栏出现（无 Dock 图标）。首次启动会自动弹出「开始之前」引导窗。
2. 点「请求授权」→ 系统弹出**屏幕与系统音频录制**授权框 → 允许。
   - macOS 把「采集系统声音」归在这项权限下。本应用只读音频流，不取画面（视频通道配置成 2×2、1fps 并直接丢弃）。
3. **授权后必须重启应用**，引导页里有「立即重启应用」按钮。
   这是 macOS 行为：TCC 的判定结果在进程启动时缓存，刚授权的进程读到的还是旧值。
4. 语音识别**不需要**单独授权（见第三节的实测结论）。
5. 首次开始会话时，系统可能下载语音识别模型和翻译语言包；翻译语言包也可以提前在
   「系统设置 › 通用 › 语言与地区 › 翻译语言」里装好。

快捷键：`⌥⌘T` 开始/暂停，`⌥⌘H` 显示/隐藏字幕窗。

### 授权踩坑记录（实测，不是推测）

这三条是 2026-11-10 本机实测 + 读 `tccd` 日志得出的，都会让人卡住：

**1. LSUIElement 应用的屏幕录制授权请求会变成通知，而且不写 TCC 数据库**

症状：点了「请求授权」什么都没发生，去系统设置的「屏幕与系统音频录制」里**找不到这个应用**，
而那个面板又没有「+」按钮，无法手动添加 —— 死局。

`tccd` 日志：

```
Notifying for access kTCCServiceScreenCapture ... promptType: 1, DB Action:None
```

`Notifying` 意味着系统发的是通知而不是模态框，被专注模式或通知设置吞掉就什么都看不到；
`DB Action:None` 意味着不写数据库，所以列表里不会出现这个应用。

修法：引导窗显示时把 `NSApp.setActivationPolicy(.regular)`（见 `OnboardingWindow.show()`），
让应用成为正常前台应用，授权请求才会弹模态框。引导结束后再切回 `.accessory`。
光调 `NSApp.activate()` 不够。

**2. ad-hoc 签名下，每次重新编译都会让屏幕录制授权失效**

TCC 按代码签名哈希记授权。本机没有任何有效的 codesigning identity
（`security find-identity -v -p codesigning` → `0 valid identities found`），
只能 ad-hoc 签名，而每次编译二进制都变，哈希也变，于是授权作废、要重新授权一次。

开发期的解法：在 Keychain 里建一个自签名的 code signing 证书并固定用它签名，
或者干脆配上 Apple Development 证书。调试管道时别频繁重编译。

清掉授权记录重新来一遍：

```bash
tccutil reset ScreenCapture com.leon.AwesomeRealtimeTranslator
```

**3. Carbon 全局快捷键似乎仍会碰 TCC**

`tccd` 日志里在注册快捷键之后出现了
`Handling access request to kTCCServiceListenEvent, Sub:{com.leon.AwesomeRealtimeTranslator}`。
原本选 `RegisterEventHotKey` 的理由就是「不需要辅助功能/输入监控权限」，
这条日志说明至少在 macOS 26 上它会触发一次 `ListenEvent` 检查。
快捷键在未授权情况下到底还能不能用，**没测**。

**4. 授权通过之后，应用会变得完全不可见（用户以为「打不开」）**

症状：授权完成、重启应用，屏幕上什么都没发生。实测进程是活着的，日志里
`应用启动 ... 屏幕录制=true`，但：

- `LSUIElement = true` → 没有 Dock 图标；
- 权限已齐 → 代码跳过引导窗，于是没有任何窗口；
- 菜单栏图标被刘海 / 一堆菜单栏项挤掉 → 唯一入口也看不见。

三件事叠起来就是零可见性。这不是发现性问题，是「应用等于打不开」。

修法（`AppDelegate.applicationDidFinishLaunching`）：权限齐了就直接
`panel.show()` 把字幕悬浮窗摆出来，哪怕还没开始听 —— 窗口自带状态点和 ▶ 按钮，
既是「我在运行」的证据，也是入口。另外补了 `applicationShouldHandleReopen`，
因为双击一个已在运行的 LSUIElement 应用默认毫无反应。

刻意**不**在启动时自动开始采集：静默录系统声音不该由程序替用户决定。
`--autostart` 参数仍然保留，只用于冒烟测试。

### 反闪烁的三条实测修正（2026-11-10）

初版用户反馈「译文一段一段出现，感觉像卡顿」。拆开是三件事，两件是缺陷：

| 现象 | 原因 | 处理 |
| --- | --- | --- |
| 译文整句蹦出 | 设计使然：只有定稿句才翻译 | 不改。边说边翻会让中文语序反复重写，比延迟更难受 |
| 每出一句，上面的行往上顶一次 | 译文行是「到了才插入」，插入瞬间 block 变高 | 译文行**始终占位**（未完成时渲染同字号省略号），单行译文高度不变 |
| 原文流式更新时整体抖动 | 每次文本变化跑一次 0.12s `easeOut` 滚动动画，动画互相打断 | 滚动**不加动画**，瞬时定位 |
| 译文出得晚 | `silenceFlushInterval = 0.7s` 偏保守 | 降到 `0.45s`。低于 0.35s 会把句子从中间切断，翻译质量明显下降 |

译文超过一行时仍会增高 —— 这是文本流排版的下限，不预测换行结果无法消除。

### `.fastResults`：原文「一段一段出现」的真正原因（2026-11-10 实测）

用户反馈「英文是一段一段出现的，不是一个词一个词出现的；对方说得快就要等一会」。
埋点量了两层：

| 层 | 数据 | 结论 |
| --- | --- | --- |
| ScreenCaptureKit 音频 | 连续 1150 块，每块 `frames=960`（= 20ms @48kHz），到达间隔稳定 21ms | 采集不是瓶颈 |
| SpeechAnalyzer 结果 | 一整句的十几条 volatile 结果**时间戳全在同一毫秒**，然后静默 3735ms / 3789ms | 瓶颈在这里 |

模型确实逐词出结果（字数 2→5→10→18→24→…），但它攒满一整句才一次性吐完。
屏幕上就是「整段蹦出来 → 等 3.7 秒 → 再蹦一段」。

原因：`SpeechTranscriber.ReportingOption` 有三个值 —— `volatileResults`、
`alternativeTranscriptions`、`fastResults`。初版只开了 `volatileResults`，
它只保证「会出 partial 结果」，**不保证出得勤**；`fastResults` 才是控制解码窗口的那个。

改成 `reportingOptions: [.volatileResults, .fastResults]` 之后：

| | 批次间隔 | 每批新增 |
| --- | --- | --- |
| 改之前 | 3735ms / 3789ms | 一整句（40~57 字符） |
| 改之后 | 871 / 934 / 908 / 959 / 960 / 968ms | 2~5 个词 |

**快了约 4 倍**，而且识别准确率顺带变好了（`onc call` → `on call`）。

剩下的 ~950ms 是模型自己的解码窗口，六次测量落在 871~968ms 之间，非常稳定，
看起来是固定的 1 秒窗口 —— 这是这套 API 的下限，不是实现问题。
### 半截词：改快之后才暴露出来的第二个 bug

`.fastResults` 上线后用户立刻发现「有些词被中间截断了」。日志里有现成证据：

```
字数=12  尾部= I am also c        ← 半截词
字数=18  尾部= I am also curious
```

volatile 结果是**逐字追加**的，`curious` 会先以 `c` / `cu` / `cur` 的形态出现。
之前 3.7 秒才刷一次，中间态一闪而过看不见；刷新变勤之后它们全暴露了 ——
**是提速把这个既有缺陷显形的，不是提速造成的。**

修法（`Segmenter.stableTail`，纯逻辑，有单测）：砍掉未定稿尾巴的最后一个 token。
关键推理是**只有最末一个词可能不完整**，它前面的词模型已经看全了，
所以砍末词就能根治，不需要词典也不需要猜。代价是原文比识别结果晚一个词（约 300ms）。

边界情况：

- 文本以空白或标点结尾 → 末词已完整，原样显示；
- 中日韩不裁剪 → 没有词边界，逐字出字本身就是正常阅读单位；
- 已定稿的头（`buffer`）绝不参与裁剪，否则会把确定的内容藏起来。

### `StreamingText`：剩下 950ms 的视觉补偿

`Sources/.../Caption/StreamingText.swift`。**这是纯障眼法**，字不会来得更早；
解码窗口是模型定的，软件能决定的只是「怎么花掉这段时间」。

第一版：400ms 内铺完一批。错的 —— 批次间隔是 950ms，于是实际节奏变成
「铺 400ms → 干等 550ms → 下一批」，只是把一次大跳换成一次小跳加一段停顿。
用户的原话是「上一句完，等一会，再出下一段」。

第二版（现在）：**速率控制**。每帧按「把当前积压在 `drainHorizon = 0.8s` 内排空」
算速度，积压多就放得快，积压少就放得慢。稳态下新的一批总在旧的一批放完之前到达，
积压恒大于零，所以字一直在动。

同时删掉了第一版里任务被取消时 `revealed = total` 的补齐 —— 那正是「每批末尾跳一下」
的来源。新批到达时进度留在原处，新任务接着往下放。

连带修一处：逐字显现时文本高度是**连续**长高的，而 `onChange` 只在批到达时触发一次，
中间的增长没人管会把内容顶到可视区以下。给 `ScrollView` 加了 `.defaultScrollAnchor(.bottom)`。

| | 显示滞后 | 观感 |
| --- | --- | --- |
| 不做补偿 | ~0 | 每 950ms 跳一次，一次几个词 |
| 第一版（400ms 铺完） | ~0.4s | 小跳 + 550ms 停顿 |
| 第二版（0.8s 排空） | ~0.8s | 连续推进，无死等 |
| **当前（0.5s 排空）** | ~0.5s | 基本连续，每批末尾约 450ms 轻微停顿 |

这个值已经做成设置项「原文显现节奏」（设置 › 显示），区间 **0.3~1.0s**，默认 0.5s：

| 取值 | 观感 |
| --- | --- |
| 0.3 | 最跟手，但能看到字一跳一跳地出现 |
| 0.5（默认） | 基本连续，每批末尾有轻微停顿 |
| 0.65~0.85 | 更顺滑，字比对方说话晚半秒以上 |
| 0.95~1.0 | 最连续，也最滞后 |

上限锁在 1.0：超过批次间隔（~950ms）之后积压只会越积越多，更滞后但**不会更顺滑**，
所以再往上调没有收益。下限 0.3 以下等于不做补偿，会退回「每秒跳一次」。
加上模型自身的 ~1s，默认值下端到端约 1.5s。

只用在**未定稿的原文尾巴**上。已定稿的句子和译文必须立刻、完整地出现 ——
让人等动画看一句已经确定的话，是拿体验换花哨。

**句子定稿瞬间的那段静止消不掉**：原文尾巴清空后，下一批 volatile 要 ~950ms 才来，
这段时间没有任何数据。任何动画在这里都是编的。

### 图标是代码画的

`Tools/MakeAppIcon.swift` 用 CoreGraphics 画 1024×1024 母图，`Tools/make-icon.sh`
切出全部尺寸并打包成 `Sources/.../Resources/AppIcon.icns`（已提交，日常构建不用跑脚本）。

这么做不是为了炫技：改配色、改比例只要改常量重跑，不用保管原始设计文件，
也不会出现「仓库里的图标和设计稿对不上」。

设计是深色方块上三条字幕条 —— 顶部半透明短条暗示已经滚过去的内容，
中间白条是原文，底部绿条是译文，绿色和字幕窗「正在听」的状态点同色。
16px 下仍然能看出是「两行字幕」。

### 字幕窗头部就是全部入口

右上角三个按钮：**▶/⏸**（也可按 `⌥⌘T`）、**⚙ 设置**、**⏻ 退出**。

它们必须在那里，这是同一个病的三个症状：菜单栏图标会被刘海或者一堆菜单栏项挤掉，
而这是 LSUIElement 应用 —— 没有 Dock 图标，字幕窗又不接受键盘焦点
（`canBecomeKey = false`，为了不抢 Zoom 的输入），所以 `⌘Q`、`⌘,` 都到不了。
没有这几个按钮，用户只能去终端 `pkill`，设置则完全打不开。

**设置窗不能用 SwiftUI 的 `Settings` 场景**（实测，踩了两次）：

`SettingsLink` 和 `NSApp.sendAction(Selector(("showSettingsWindow:")))` 都要走
**responder chain**，而 `sendAction(to: nil)` 是从 key window 开始找目标的。
这个应用没有 key window —— 字幕面板 `canBecomeKey = false`（不能抢 Zoom 的键盘输入），
又是 LSUIElement，没有 Dock 图标也没有主菜单。责任链是空的，`sendAction` 返回 false，
**点设置毫无反应**。

所以 `Settings` 场景直接删掉，改成自己管的 `SettingsWindow`（`NSWindow` +
`NSHostingController`，和 `OnboardingWindow` 同一套路），绕开整条责任链。
菜单栏的「设置…」也指向同一个窗口，避免出现两个内容相同的设置窗。

并且**刻意不**把激活策略提成 `.regular`：那会长出 Dock 图标，而 Dock 会被屏幕共享
录进去，违反「对方看不到任何东西」这个前提。

## 三、对 PRD 的偏离，以及原因

PRD §9.1 要求先核实 Apple API 再写。核实方式是直接读 SDK 26.5 的 `.swiftinterface`
（比文档权威，带 `@available` 标注）：

```
$(xcrun --sdk macosx --show-sdk-path)/System/Library/Frameworks/Translation.framework/Modules/Translation.swiftmodule/arm64e-apple-macos.swiftinterface
```

### 1. 部署目标从 macOS 15 改成 macOS 26（最重要的一条）

| 事实 | 来源 |
|---|---|
| `TranslationSession(installedSource:target:)` 标注 `@available(iOS 26.0, macOS 26.0, *)` | Translation.swiftinterface:188 |
| `TranslationSession.cancel()` / `canRequestDownloads` 同样是 26.0+ | 同上 |
| `SpeechAnalyzer` / `SpeechTranscriber` / `AssetInventory` / `AnalysisContext` 全部 `@available(macOS 26.0, *)` | Speech.swiftinterface |

在 macOS 15 上 Translation 框架虽然存在，但**唯一**拿到 `TranslationSession` 的途径是 SwiftUI 的
`.translationTask(configuration)` 修饰器——意味着翻译必须挂在一个活着的 View 上，
非 UI 层拿不到会话。要在 macOS 15 上跑，得额外维护一个隐藏 View 做翻译中转，
同时识别侧退回 `SFSpeechRecognizer`，把下面 #2 的 bug 全部请回来。

结论：**PRD §10 的「最低系统版本」这个未决问题，定在 macOS 26。** 代价是放弃 macOS 15 用户；
收益是砍掉两类难点和一半代码。协议层（`Transcriber` / `Translator`）保持原样，
以后要补 macOS 15 回退路径，加一个实现即可，不动管道。

### 2. PRD 7.4 #2「识别会话时长限制」在新 API 下不存在，所以没写无缝重启

`SFSpeechRecognitionTask` 是「一次请求一段话」的模型，停顿后任务自己结束，必须重建请求，
重建窗口里的音频会丢——这是 PRD 担心的问题。`SpeechAnalyzer` 是常驻 actor，
输入是一条永不结束的 `AsyncSequence`，模型内部维护 volatile / finalized 区间，
没有「会话时长」这个概念。F3 里「识别会话结束时自动无缝重启」这条验收标准因此无需实现。

### 3. 术语表走官方上下文入口，不是 prompt 拼接

PRD F11 写的是「作为识别器的上下文提示词」。实际 API 是
`AnalysisContext.contextualStrings`（`[.general: [String]]`），由模型在解码阶段加权，
比拼 prompt 可靠。管道已经接好（`AppSettings.glossary` → `SpeechAnalyzerTranscriber`），
只差一个编辑 UI，留给 M3。

### 4. 工程用 XcodeGen 生成，而不是手写 `.xcodeproj`

PRD §9.2 要求「创建一个 Xcode 工程」。结果是一样的（`.xcodeproj` 在仓库里，能直接打开），
但单一事实源是 18 行的 `project.yml`，不是 1000 行的 pbxproj。

### 5. 单元测试目标不挂宿主 App

`Segmenter` 是纯逻辑，测试 target 直接把 `Segmenter.swift` 和 `Transcriber.swift` 编进测试
bundle，不需要启动 GUI 应用。`xcodebuild test` 在 0.01 秒内跑完 27 个用例。

### 6. 两处为 Swift 6 完全并发检查做的设计调整

- `Transcriber.append(_:)` 是**同步非隔离**方法。音频回调每 10~20ms 一次，
  如果要 `await` 跨 actor，就得为每块音频开一个 Task，纯调度浪费。
  实现方用一个带锁的 sink（`AudioInputSink`）直接转交给 `AsyncStream.Continuation`。
- `AppleTranslator` 是 `final class` 而不是 `actor`。`TranslationSession` 是 non-Sendable 的类，
  从 actor 里 `await session.translate(...)` 等于把 non-Sendable 对象跨隔离域传递，
  Swift 6 直接拒绝编译。改成非隔离类 + 锁保护引用；调用方本身是串行队列。

### 7. 超出 M1 范围、顺手做掉的

F7（三种显示模式 / 字号 / 透明度 / 行数）、F8（语言下拉）、F9（菜单栏 + 全局快捷键）、
以及 F10 的一半（当前会话的复制 / 导出 Markdown、可选落盘）。
理由：这些是面试前最后一秒会调的东西，不做的话 M1 没法真的拿去用一次。
**没做**：历史会话列表窗口、内购与限时、首次引导的「播放示例音频测试」这一步、多语言界面。

### 8. PRD §10 其余未决问题的处理

| 问题 | 本版的决定 | 理由 |
|---|---|---|
| 屏幕共享时隐藏字幕窗 | **做了，默认开** | `NSWindow.sharingType = .none`。不需要检测共享状态——窗口对所有录屏/共享 API 从源头不可见，你自己照常看得见。顺带保证字幕不会被自己采集进去 |
| 会话记录默认行为 | **默认不保存** | 面试记录含对方可识别信息，默认留痕对隐私不友好。想复盘的人自己打开；当前这场随时能复制/导出 |
| 首发语言对 | 英→中**已接通**，法/德/日/西班牙语在 UI 里但未实测 | 识别和翻译都是系统能力，加语言只是改 identifier |
| 产品名称、定价、免费版限时 | **没做，代码里没有任何计时或内购** | 定价要先做竞品调研（PRD 自己也列为待办）。限时逻辑一行没写，v1.0 要加时再加 |

---

## 四、已经验证的 / 还没验证的

诚实分界线，别把下面两栏混着看。

**已验证**

- Swift 6 完全并发检查（`SWIFT_STRICT_CONCURRENCY=complete`）下零错误编译。
- `Segmenter` 27 个单元测试全过，包括缩写、小数点、省略号、引号、静音定稿、超长硬切、
  volatile 覆盖语义、以及一段端到端事件脚本（PRD §9.6 的验收形式）。
  其中 3 个 bug 是测试逮出来的（`no.` 被误判成缩写、省略号后切不开、切句残留前导空格）。
- Release 配置打包成 `.app`，启动后进程存活、无崩溃报告、菜单栏图标正常。

**没验证（需要你本机跑一次）**

1. **系统音频实际有没有抓到声音**。ScreenCaptureKit 需要 TCC 授权，必须你点同意，
   我没法在这边完成。这是最大的未验证风险点。
2. 识别准确率、端到端延迟（PRD 目标 <1s / 译文 <1.5s）。
3. 翻译语言包未安装时的下载流程（`canRequestDownloads` + `prepareTranslation()` 这条路径）。
4. 60 分钟连续运行的稳定性与 CPU 占用（PRD 目标 <30% 单核）。
5. 插拔耳机 / 切换输出设备后的自动恢复（代码里有 `didStopWithError` 重启 + 12 秒无音频看门狗，逻辑没实测）。
6. 中文作为**源语言**时的识别效果。
7. 「语音识别无需单独授权」这个结论：代码里按 `SpeechTranscriber.isAvailable` 为真就返回
   `.notRequired`。如果你实测发现仍然弹 Siri 授权框，把 `Permissions.speechRecognitionState`
   改成读 `legacySpeechState` 即可。

## 五、手动测试步骤

最小验证（不用开 Zoom）：

1. 启动应用，授权屏幕录制，重启应用。
2. 菜单栏点「开始听」，字幕窗出现在屏幕顶部居中。
3. 在终端跑一句，让系统朗读（`say` 是独立进程，会被系统音频采集到）：

```bash
say -v Samantha "Tell me about your experience with distributed systems. How many nodes did you run in production?"
```

4. 预期：原文边说边出字，句末标点处定稿，下面出现中文译文；译文出现后不再改写。
5. 测鼠标穿透：菜单里开「鼠标穿透字幕窗」，点字幕窗应该点穿到后面的应用。
6. 测全屏可见：把 Safari 切全屏，字幕窗应该还在最上面。
7. 测屏幕共享隐藏：开 QuickTime「新建屏幕录制」，预览里应该看不到字幕窗。
8. 停止会话，菜单里「复制本场记录」，粘贴出来应该是带时间戳的双语文本。

正式验证（PRD M2 的完成标志）：和朋友开 45 分钟 Zoom 模拟面试，全程不中断，记录可导出。

## 六、目录结构

```
Sources/AwesomeRealtimeTranslator/
├── App/           AppCoordinator（管道装配）、菜单栏、全局快捷键、App 入口
├── Audio/         AudioSource 协议、SystemAudioCapture（ScreenCaptureKit）、PCM 格式转换
├── Transcription/ Transcriber 协议、SpeechAnalyzerTranscriber、AudioInputSink
├── Segmentation/  Segmenter（纯逻辑，有单测）
├── Translation/   Translator 协议、AppleTranslator、PassthroughTranslator（降级用）
├── Caption/       CaptionStore（@Observable）、CaptionView、CaptionPanelController（NSPanel）
├── Permissions/   权限检测、引导窗
└── Settings/      AppSettings、设置界面、会话记录落盘
```

数据流：

```
SystemAudioCapture ──PCM──► SpeechAnalyzerTranscriber ──TranscriptEvent──► Segmenter
                                                                              │ Sentence
                                                                              ▼
                              CaptionPanel ◄── CaptionStore ◄── 翻译串行队列（Translator）
```

两条反闪烁规则（PRD 7.4 #1）：

- 原文区显示「已定稿句子 + 当前未定稿尾句」，只有最后一行会变。
- 译文**只**对已定稿句子生成，且走串行队列，保证顺序与原文一致，译文一旦出现就不再改写。

## 七、隐私实现方式

不是口头承诺，是技术约束：

- `.entitlements` 里**没有** `com.apple.security.network.client`。沙盒从系统层面阻断任何出网请求。
- 音频只在内存里流过识别器，不写文件，没有录音代码路径。
- 会话文本默认不落盘（设置里可开）。
- 字幕窗 `sharingType = .none`，不会出现在任何录屏或共享画面里。

以后做 BYOK 翻译（F12）必须打开网络权限，届时要在设置页明确提示，并且默认关闭。

## 八、许可证

[MIT](LICENSE)。
