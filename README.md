# Awesome-Realtime-Translator

**Site and download**: <https://yangli-0409.github.io/Awesome-Realtime-Translator/> ·
[中文 README](README.zh-CN.md)

Real-time interview captions for macOS. It captures whatever your computer is playing,
then shows the source transcript and a Chinese translation in a floating panel.
Everything runs on-device; the sandbox grants no network access at all. It never joins
the meeting, and the other side sees nothing.

Scope: `M1 prototype` (F1–F6) plus F7/F8/F9, which came almost for free.

**Product boundary: it helps you understand, it does not answer for you.**
There is no "suggested answer" feature and there will not be one.

---

## 1. Build and run

```bash
cd Awesome-Realtime-Translator && xcodegen generate && open AwesomeRealtimeTranslator.xcodeproj
```

Build and test from the command line:

```bash
xcodebuild -project AwesomeRealtimeTranslator.xcodeproj -scheme AwesomeRealtimeTranslator -destination 'platform=macOS' test
```

The project is generated from `project.yml` (XcodeGen). The `.xcodeproj` is committed too,
so you can open it without installing XcodeGen. Change the file layout and just re-run
`xcodegen generate` — no pbxproj merge conflicts.

**Requirements**: macOS 26.0+, Xcode 26+, Apple Silicon. Section 3 explains why this is not
macOS 15 as the PRD asked.

## 2. First-run authorization (F2)

1. The app lives in the menu bar only — no Dock icon. On first launch it opens a
   "Before you start" onboarding window.
2. Click **Request access** → macOS shows the **Screen & System Audio Recording** prompt → allow.
   - macOS files "capture system audio" under that permission. The app reads the audio stream
     only; the video channel is configured at 2×2 pixels, 1 fps, and every frame is discarded.
3. **Restart the app after granting.** The onboarding page has a restart button.
   This is macOS behaviour: TCC decisions are cached when the process starts, so a
   freshly authorized process still reads the stale value.
4. Speech recognition needs **no** separate authorization — see the measured finding in section 3.
5. On the first session macOS may download the speech model and the translation language pack.
   You can install the language pack ahead of time in
   *System Settings › General › Language & Region › Translation Languages*.

Shortcuts: `⌥⌘T` start/pause, `⌥⌘H` show/hide the caption panel.

### Authorization pitfalls (measured, not guessed)

All four come from running this on a real machine on 2026-11-10 and reading `tccd` logs.
Every one of them is a dead end if you do not know about it.

**1. For an LSUIElement app, the screen-recording prompt degrades to a notification — and writes no TCC row**

Symptom: you click "Request access" and nothing happens. You go to
*Screen & System Audio Recording* in System Settings and **the app is not in the list** —
and that pane has no "+" button, so you cannot add it manually. Complete dead end.

From `tccd`:

```
Notifying for access kTCCServiceScreenCapture ... promptType: 1, DB Action:None
```

`Notifying` means the system posted a notification instead of a modal dialog; if Focus mode
or notification settings swallow it, you see nothing. `DB Action:None` means no database row
is written, which is why the app never shows up in the list.

Fix: call `NSApp.setActivationPolicy(.regular)` while the onboarding window is up
(see `OnboardingWindow.show()`) so the app becomes a normal foreground app and the request
produces a modal dialog. Switch back to `.accessory` when onboarding ends.
`NSApp.activate()` alone is not enough.

**2. With ad-hoc signing, every rebuild revokes the screen-recording grant**

TCC keys authorization to the code-signing hash. This machine has no valid codesigning identity
(`security find-identity -v -p codesigning` → `0 valid identities found`), so the only option is
ad-hoc signing — and every build changes the binary, which changes the hash, which voids the grant.

For development: create a self-signed code-signing certificate in Keychain and always sign with it,
or just set up an Apple Development certificate. While debugging the pipeline, avoid frequent rebuilds.

To reset and start over:

```bash
tccutil reset ScreenCapture com.leon.AwesomeRealtimeTranslator
```

**3. Carbon global hotkeys appear to still touch TCC**

After registering the hotkeys, `tccd` logs
`Handling access request to kTCCServiceListenEvent, Sub:{com.leon.AwesomeRealtimeTranslator}`.
The whole reason for picking `RegisterEventHotKey` was that it needs no Accessibility or
Input Monitoring permission; this log says that on macOS 26 it triggers a `ListenEvent` check
anyway. Whether the hotkeys still work without that grant is **untested**.

**4. Once authorized, the app becomes completely invisible (users read this as "it won't open")**

Symptom: you grant access, restart, and nothing appears. The process is in fact alive and logs
`app launched ... screenRecording=true`, but:

- `LSUIElement = true` → no Dock icon;
- permission is now granted → the code skips onboarding, so there is no window at all;
- the menu bar icon is hidden behind the notch or crowded out → the only entry point is invisible.

Stack those three and you get zero visibility. This is not a discoverability problem;
the app is effectively unopenable.

Fix (`AppDelegate.applicationDidFinishLaunching`): if permission is granted, call `panel.show()`
immediately — show the caption panel even before listening starts. It carries a status dot and a
▶ button, so it is both the proof that the app is running and the way in. Also added
`applicationShouldHandleReopen`, because double-clicking an already-running LSUIElement app
does nothing by default.

Deliberately **not** auto-starting capture at launch: silently recording system audio is not a
decision a program should make for the user. The `--autostart` flag stays, for smoke tests only.

### Three anti-flicker fixes (measured, 2026-11-10)

First user report: "the translation appears in chunks, it feels like stuttering."
That turned out to be three separate things, two of which were real defects.

| Symptom | Cause | Resolution |
| --- | --- | --- |
| Translation appears a whole sentence at a time | By design: only finalized sentences get translated | Unchanged. Translating mid-sentence makes Chinese word order rewrite itself repeatedly, which is worse than the delay |
| Every new line shoves the lines above it upward | The translation row was inserted on arrival, growing the block | The translation row **always reserves space** (an ellipsis at the same font size while pending), so a one-line translation changes no heights |
| The whole view jitters while the source streams | Every text change ran a 0.12s `easeOut` scroll animation, and the animations interrupted each other | Scroll with **no animation**, instant positioning |
| Translation arrives late | `silenceFlushInterval = 0.7s` was too conservative | Lowered to `0.45s`. Below 0.35s sentences get cut mid-thought and translation quality drops visibly |

A translation longer than one line still grows the block. That is the floor of text layout —
you cannot avoid it without predicting line breaks.

### `.fastResults`: the real reason the source text arrived in chunks (measured, 2026-11-10)

Next report: "the English shows up in chunks, not word by word; if they speak fast I have to wait."
Instrumented both layers:

| Layer | Data | Conclusion |
| --- | --- | --- |
| ScreenCaptureKit audio | 1150 consecutive buffers, each `frames=960` (20ms @48kHz), arriving every 21ms | Capture is not the bottleneck |
| SpeechAnalyzer results | A dozen volatile results for one sentence, **all stamped in the same millisecond**, then 3735ms / 3789ms of silence | This is the bottleneck |

The model really does emit word by word (character counts 2→5→10→18→24→…), but it buffers a
whole sentence and flushes it at once. On screen that reads as "a chunk appears → wait 3.7s →
another chunk."

Cause: `SpeechTranscriber.ReportingOption` has three values — `volatileResults`,
`alternativeTranscriptions`, `fastResults`. The first version only set `volatileResults`,
which guarantees that partial results *exist*, **not that they arrive often**.
`fastResults` is the one that controls the decode window.

After switching to `reportingOptions: [.volatileResults, .fastResults]`:

| | Batch interval | New text per batch |
| --- | --- | --- |
| Before | 3735ms / 3789ms | A whole sentence (40–57 chars) |
| After | 871 / 934 / 908 / 959 / 960 / 968ms | 2–5 words |

**About 4× faster**, and recognition accuracy improved as a side effect (`onc call` → `on call`).

The remaining ~950ms is the model's own decode window. Six measurements landed between 871 and
968ms — remarkably stable, apparently a fixed one-second window. That is this API's floor,
not an implementation problem.

### Half-words: a second bug that only speed made visible

Right after `.fastResults` shipped, the next report was "some words are cut off in the middle."
The logs already had the evidence:

```
len=12  tail= I am also c        ← half a word
len=18  tail= I am also curious
```

Volatile results are **appended character by character**, so `curious` first appears as
`c` / `cu` / `cur`. At one refresh every 3.7 seconds those intermediate states flashed by
unseen; refreshing four times as often exposed all of them.
**Speeding things up revealed an existing defect rather than causing a new one.**

Fix (`Segmenter.stableTail`, pure logic, unit-tested): drop the last token of the volatile tail.
The key insight is that **only the final token can be incomplete** — the model has already seen
every word before it — so dropping it fixes the problem outright, with no dictionary and no
guessing. The cost is that the source lags the recognizer by one word (~300ms).

Edge cases:

- text ends in whitespace or punctuation → the last word is complete, show everything;
- CJK is never trimmed → no word boundaries, and character-by-character *is* the natural reading unit;
- the finalized head (`buffer`) never participates in trimming, or we would hide settled content.

### `StreamingText`: visual compensation for the remaining 950ms

`Sources/.../Caption/StreamingText.swift`. **This is pure sleight of hand** — text does not
arrive any earlier. The decode window belongs to the model; all software can decide is how to
spend that time.

Version one: reveal each batch over 400ms. Wrong — batches arrive every 950ms, so the actual
rhythm became "reveal for 400ms → sit still for 550ms → next batch." That traded one big jump
for a small jump plus a pause. The user's words: "one finishes, you wait a bit, then the next
chunk shows up."

Version two (current): **rate control**. Each frame computes the speed needed to drain the
current backlog within `drainHorizon`; a big backlog drains fast, a small one slowly. In steady
state a new batch always arrives before the previous one has drained, so the backlog never hits
zero and the text never stops moving.

Also deleted version one's `revealed = total` catch-up on task cancellation — that was exactly
what made every batch jump at the end. Now progress stays where it is and the next task
continues from there.

One knock-on fix: during the reveal the text grows **continuously**, while `onChange` only fires
once per batch, so the growth in between pushed content below the visible area. Added
`.defaultScrollAnchor(.bottom)` to the `ScrollView`.

| | Display lag | Feel |
| --- | --- | --- |
| No compensation | ~0 | Jumps every 950ms, several words at a time |
| v1 (400ms reveal) | ~0.4s | Small jump plus a 550ms pause |
| v2 (0.8s drain) | ~0.8s | Continuous, no dead time |
| **Current (0.5s drain)** | ~0.5s | Mostly continuous, ~450ms of slight pause at the end of each batch |

This is now a setting, "source reveal pace" (*Settings › Display*), ranging **0.3–1.0s**,
default 0.5s:

| Value | Feel |
| --- | --- |
| 0.3 | Most responsive, but you see the text jump |
| 0.5 (default) | Mostly continuous, slight pause per batch |
| 0.65–0.85 | Smoother; text trails speech by more than half a second |
| 0.95–1.0 | Most continuous, most delayed |

The ceiling is locked at 1.0: past the batch interval (~950ms) the backlog only grows,
which adds lag **without adding smoothness**, so there is nothing to gain. Below 0.3 you are
effectively not compensating at all and fall back to "jump once a second."
With the model's own ~1s, the default gives roughly 1.5s end to end.

Used only on the **unfinalized source tail**. Finalized sentences and translations must appear
instantly and in full — making someone watch an animation spell out a sentence that is already
settled trades usability for flourish.

**The pause at the moment a sentence finalizes cannot be removed**: the source tail empties and
the next volatile batch is ~950ms away, so there is no data during that window.
Any animation there would be invented.

### The icon is drawn in code

`Tools/MakeAppIcon.swift` draws the 1024×1024 master with CoreGraphics; `Tools/make-icon.sh`
cuts every size and packs `Sources/.../Resources/AppIcon.icns` (committed, so normal builds
never run the script).

Not for show: changing a colour or a proportion means editing a constant and re-running, with no
source design file to keep track of and no way for the repo's icon to drift from the design.

The design is three caption bars on a dark plate — a short translucent bar on top hinting at
content that already scrolled past, a white bar for the source, a green bar for the translation,
in the same green as the "listening" status dot. At 16px it still reads as "two lines of captions."

### The caption panel header is the entire UI

Three buttons, top right: **▶/⏸** (also `⌥⌘T`), **⚙ Settings**, **⏻ Quit**.

They have to be there, and they are three symptoms of one condition: the menu bar icon gets
hidden behind the notch or crowded out by other menu extras, and this is an LSUIElement app —
no Dock icon, and the caption panel refuses keyboard focus (`canBecomeKey = false`, so it never
steals input from Zoom), which means `⌘Q` and `⌘,` never reach it. Without these buttons a user
can only `pkill` from the terminal, and Settings cannot be opened at all.

**Settings cannot use SwiftUI's `Settings` scene** (learned the hard way, twice):

`SettingsLink` and `NSApp.sendAction(Selector(("showSettingsWindow:")))` both travel the
**responder chain**, and `sendAction(to: nil)` starts looking from the key window. This app has
no key window — the caption panel sets `canBecomeKey = false`, and as an LSUIElement app it has
neither a Dock icon nor a main menu. The chain is empty, `sendAction` returns false, and
**clicking Settings does nothing at all.**

So the `Settings` scene was deleted in favour of a hand-managed `SettingsWindow`
(`NSWindow` + `NSHostingController`, the same pattern as `OnboardingWindow`), bypassing the
responder chain entirely. The menu bar's "Settings…" points at the same window, so there is
never a second copy.

And deliberately **not** raising the activation policy to `.regular`: that grows a Dock icon,
and the Dock does get captured by screen sharing, which breaks the premise that the other side
sees nothing.

## 3. Deviations from the PRD, and why

PRD §9.1 asks that Apple APIs be verified before writing code. Verification was done by reading
the SDK 26.5 `.swiftinterface` files directly — more authoritative than the docs, and annotated
with `@available`:

```
$(xcrun --sdk macosx --show-sdk-path)/System/Library/Frameworks/Translation.framework/Modules/Translation.swiftmodule/arm64e-apple-macos.swiftinterface
```

### 1. Deployment target raised from macOS 15 to macOS 26 (the big one)

| Fact | Source |
|---|---|
| `TranslationSession(installedSource:target:)` is marked `@available(iOS 26.0, macOS 26.0, *)` | Translation.swiftinterface:188 |
| `TranslationSession.cancel()` and `canRequestDownloads` are 26.0+ as well | same file |
| `SpeechAnalyzer` / `SpeechTranscriber` / `AssetInventory` / `AnalysisContext` are all `@available(macOS 26.0, *)` | Speech.swiftinterface |

The Translation framework does exist on macOS 15, but the **only** way to obtain a
`TranslationSession` there is SwiftUI's `.translationTask(configuration)` modifier — meaning
translation must hang off a live View, and non-UI layers cannot get a session. Running on
macOS 15 would require an extra hidden View acting as a translation relay, plus falling back to
`SFSpeechRecognizer` on the recognition side, which brings back every problem in #2 below.

Conclusion: **the PRD §10 open question about the minimum OS version is settled at macOS 26.**
The cost is giving up macOS 15 users; the gain is dropping two classes of difficulty and half
the code. The protocols (`Transcriber` / `Translator`) are untouched, so adding a macOS 15
fallback later means adding one implementation without touching the pipeline.

### 2. PRD 7.4 #2 "recognition session time limit" does not exist with the new API, so there is no seamless restart

`SFSpeechRecognitionTask` follows a "one request per utterance" model: it ends itself after a
pause, you must rebuild the request, and audio inside the rebuild window is lost — which is what
the PRD was worried about. `SpeechAnalyzer` is a long-lived actor fed by an `AsyncSequence` that
never ends; it maintains volatile and finalized ranges internally and has no notion of session
duration. The F3 acceptance criterion about "seamlessly restarting when the recognition session
ends" therefore needs no implementation.

### 3. The glossary uses the official context entry point, not prompt concatenation

PRD F11 describes it as "a context prompt for the recognizer." The actual API is
`AnalysisContext.contextualStrings` (`[.general: [String]]`), which the model weights during
decoding — more reliable than stitching a prompt. The pipeline is already wired
(`AppSettings.glossary` → `SpeechAnalyzerTranscriber`); only the editing UI is missing, left for M3.

### 4. The project is generated by XcodeGen rather than hand-written `.xcodeproj`

PRD §9.2 asks for "an Xcode project." The outcome is the same — the `.xcodeproj` is in the repo
and opens directly — but the single source of truth is an 18-line `project.yml` instead of a
1000-line pbxproj.

### 5. The test target has no host app

`Segmenter` is pure logic, so the test target compiles `Segmenter.swift` and `Transcriber.swift`
straight into the test bundle; no GUI app has to launch. `xcodebuild test` runs all cases in
about 0.01 seconds.

### 6. Two design changes forced by Swift 6 strict concurrency

- `Transcriber.append(_:)` is a **synchronous, non-isolated** method. The audio callback fires
  every 10–20ms; if it had to `await` across an actor, every buffer would need its own Task —
  pure scheduling waste. The implementation hands buffers to a lock-guarded sink
  (`AudioInputSink`) that forwards to an `AsyncStream.Continuation`.
- `AppleTranslator` is a `final class`, not an `actor`. `TranslationSession` is a non-Sendable
  class, so `await session.translate(...)` from inside an actor passes a non-Sendable object
  across an isolation boundary, which Swift 6 refuses to compile. It became a non-isolated class
  with a lock around the reference; the caller is already a serial queue.

### 7. Beyond M1 scope, done anyway

F7 (three display modes / font size / opacity / line count), F8 (language pickers),
F9 (menu bar plus global hotkeys), and half of F10 (copy and export the current session as
Markdown, optional on-disk transcripts). Reason: these are the things you adjust in the last
minute before an interview — without them M1 cannot actually be used for real.

**Not done**: the session history window, in-app purchase and trial limits, the onboarding step
that plays sample audio, and a localized UI.

### 8. How the remaining PRD §10 open questions were handled

| Question | Decision | Reason |
|---|---|---|
| Hide the caption panel during screen sharing | **Done, on by default** | `NSWindow.sharingType = .none`. No need to detect sharing — the window is invisible to every recording and sharing API at the source, while you still see it normally. It also guarantees the captions are never captured back into our own audio/video pipeline |
| Default behaviour for transcripts | **Not saved by default** | Interview transcripts contain identifiable information about the other party; leaving traces by default is unfriendly. Anyone who wants to review can turn it on, and the current session can always be copied or exported |
| Launch language pair | English → Chinese **verified**; French / German / Japanese / Spanish are in the UI but untested | Both recognition and translation are system capabilities, so adding a language is just a different identifier |
| Product name, pricing, free-tier limits | **Not done; there is no timer or IAP code anywhere** | Pricing needs competitive research first (the PRD lists this as a to-do). Not a single line of trial-limit logic exists; it can be added when v1.0 needs it |

---

## 4. What is verified, and what is not

An honest dividing line. Do not read the two lists as one.

**Verified**

- Compiles with zero errors under Swift 6 strict concurrency (`SWIFT_STRICT_CONCURRENCY=complete`).
- All 27 `Segmenter` unit tests pass, covering abbreviations, decimal points, ellipses, closing
  quotes, silence-triggered finalization, forced long cuts, volatile replacement semantics,
  trailing-word trimming, and a full end-to-end event script (the acceptance form from PRD §9.6).
  Three of those tests caught real bugs (`no.` misread as an abbreviation, ellipsis preventing a
  split, a leading space left behind after a cut).
- Release build packaged as a `.app`: process stays alive, no crash reports, menu bar icon works.
- End-to-end pipeline on a real machine: capture → recognition → segmentation → translation →
  caption panel, with no errors in the logs across repeated runs.

**Not verified (needs a real session on your machine)**

1. Recognition accuracy and end-to-end latency against the PRD targets (<1s source, <1.5s translation).
   The measured ~1.5s above comes from synthesized speech via `say`, which has unnaturally even
   pacing — real speakers interrupt, hesitate and accelerate.
2. The download path for a missing translation language pack
   (`canRequestDownloads` + `prepareTranslation()`).
3. Stability and CPU use over 60 continuous minutes (PRD target: <30% of one core).
4. Automatic recovery after unplugging headphones or switching output devices. The code has a
   `didStopWithError` restart plus a 12-second no-audio watchdog, but the logic is untested.
5. Recognition quality with Chinese as the **source** language.
6. The conclusion that speech recognition needs no separate authorization: the code returns
   `.notRequired` whenever `SpeechTranscriber.isAvailable` is true. If you do see a Siri
   authorization prompt, change `Permissions.speechRecognitionState` to read `legacySpeechState`.
7. Whether the Carbon hotkeys work without Input Monitoring access (see pitfall 3 above).

## 5. Manual test procedure

Minimal verification, no Zoom required:

1. Launch the app, grant screen recording, restart the app.
2. Click ▶ in the caption panel; it sits at the top center of the screen.
3. Run this in a terminal so the system speaks (`say` is a separate process, so its audio is
   picked up by system audio capture):

```bash
say -v Samantha "Tell me about your experience with distributed systems. How many nodes did you run in production?"
```

4. Expected: the source text advances while it speaks, finalizes at sentence-ending punctuation,
   and a Chinese translation appears below it. Once a translation appears it is never rewritten.
5. Click-through: enable "click-through" in settings; clicking the panel should hit the app behind it.
6. Full-screen visibility: put Safari in full screen; the panel should stay on top.
7. Screen-sharing invisibility: start QuickTime's "New Screen Recording"; the panel should be
   absent from the preview.
8. Stop the session, choose "Copy this session," and paste — you should get timestamped
   bilingual text.

Full verification (the completion marker for PRD M2): a 45-minute mock interview over Zoom with
a friend, uninterrupted, with an exportable transcript.

## 6. Layout

```
Sources/AwesomeRealtimeTranslator/
├── App/           AppCoordinator (pipeline assembly), menu bar, global hotkeys, app entry point
├── Audio/         AudioSource protocol, SystemAudioCapture (ScreenCaptureKit), PCM conversion
├── Transcription/ Transcriber protocol, SpeechAnalyzerTranscriber, AudioInputSink
├── Segmentation/  Segmenter (pure logic, unit-tested)
├── Translation/   Translator protocol, AppleTranslator, PassthroughTranslator (graceful degradation)
├── Caption/       CaptionStore (@Observable), CaptionView, StreamingText, CaptionPanelController
├── Permissions/   Permission checks, onboarding window
└── Settings/      AppSettings, settings window, transcript archiving
```

Data flow:

```
SystemAudioCapture ──PCM──► SpeechAnalyzerTranscriber ──TranscriptEvent──► Segmenter
                                                                              │ Sentence
                                                                              ▼
                        CaptionPanel ◄── CaptionStore ◄── serial translation queue (Translator)
```

The two anti-flicker rules (PRD 7.4 #1):

- The source area shows "finalized sentences + the current unfinalized tail"; only the last line changes.
- Translations are generated **only** for finalized sentences and dispatched through a serial
  queue, so their order matches the source and a translation is never rewritten once shown.

## 7. How privacy is enforced

Not a promise — a technical constraint:

- The `.entitlements` file does **not** contain `com.apple.security.network.client`.
  The sandbox blocks outbound requests at the OS level.
- Audio only streams through the recognizer in memory. There is no code path that writes a file.
- Session text is not persisted by default (there is a setting to opt in).
- The caption panel sets `sharingType = .none`, so it never appears in any recording or shared screen.

A future BYOK translation backend (F12) would require turning on network access; when that
happens it must be stated explicitly in settings and default to off.

## License

Not yet chosen. Until one is added, no permissions are granted beyond reading the source.
