# Awesome-Realtime-Translator

Real-time captions for English interviews, on your Mac.

**[Download and website](https://yangli-0409.github.io/Awesome-Realtime-Translator/)** ·
[中文 README](README.zh-CN.md) · [Engineering notes](docs/ENGINEERING.md) ·
[Windows port](windows/README.md)

It listens to whatever your computer is playing and shows the source transcript plus a Chinese
translation in a floating panel on top of your screen. Everything runs on your own machine.
It never joins the meeting, and the other side sees nothing.

**It helps you understand. It does not answer for you.** There is no "suggested answer"
feature and there will not be one.

---

## What it does

**Captions in real time.** The source text advances word by word as the other person speaks.
A sentence gets translated once it is finished, and a translation is never rewritten after it
appears — no text jumping around while you are trying to read it.

**Invisible to the other side.** The caption panel never shows up in any screen recording or
screen share, including your own. This is not "hide when sharing is detected" — it cannot be
captured in the first place, even when you share your whole desktop.

**No network, at all.** Speech and translation both run on your Mac. The app has no ability to
reach the network, so it works with the Wi-Fi off. That is not a promise, it is a constraint
built into how it ships.

**Nothing left behind.** Audio is heard and dropped; it is never written to disk. Transcripts
are off by default. If you do want to keep a session, you can copy it or export the bilingual
text as Markdown.

**Stays out of your way.** It takes no keyboard focus, so typing in Zoom is never interrupted.
No Dock icon. Optional click-through, so the mouse goes straight past it. It stays on top even
when another app is full screen, and you can drag it anywhere.

**Adjustable until it looks right.** Three display modes (bilingual / source only / translation
only), font size, opacity, how many lines to keep, and how fast the text reveals itself.

## How fast it is

| | |
| --- | --- |
| Source text | trails the speaker by about one second |
| Translation | appears about half a second after they finish a sentence |
| **End to end** | **about 1.5 seconds** from "they stop talking" to "you have read it" |

About one of those seconds is Apple's on-device speech engine deciding what it heard. Software
cannot shorten it, and a faster Mac will not either. What the app does control is the *rhythm*:
instead of dumping a batch of words every second, it paces them out so the text flows
continuously. That pacing is a slider (0.3–1.0s, default 0.5s) in *Settings › Display* — lower
is more responsive but visibly steppy, higher is smoother but lags further behind.

## Install

Needs **macOS 26 or newer**. Universal binary (Apple Silicon and Intel), 0.8 MB.

1. [Download](https://yangli-0409.github.io/Awesome-Realtime-Translator/AwesomeRealtimeTranslator-0.1.0.zip),
   unzip, drag into Applications.
2. **Open it with right-click the first time.** macOS will say the developer cannot be verified,
   because there is no Apple developer certificate behind this build. Right-click the app →
   **Open** → confirm. Once only.
3. Allow **Screen & System Audio Recording** when asked. macOS files "listen to what the
   computer is playing" under that permission; there is no separate "system audio" option to
   grant. The app takes the audio and discards every video frame.
4. Click **Restart app now** on the onboarding page. A permission you just granted only takes
   effect after a restart.
5. The caption panel appears at the top center of the screen. Click ▶ to start listening.

On the first session macOS may download the speech model and the translation language pack. You
can install the language pack ahead of time in *System Settings › General › Language & Region ›
Translation Languages*.

## Controls

Everything lives in the caption panel's own header — do not rely on the menu bar icon, it gets
hidden behind the notch.

| | Where | Shortcut |
| --- | --- | --- |
| Start / pause | ▶ top left of the panel | `⌥⌘T` |
| Show / hide the panel | — | `⌥⌘H` |
| Settings | ⚙ top right of the panel | — |
| Quit | ⏻ top right of the panel | — |
| Move the panel | drag it anywhere | — |

## Known limitations

- **macOS 26 and up only.** It relies on on-device speech and translation that older versions
  do not have.
- **About 1.5 seconds behind.** That is the speed limit of the speech engine, not a tuning issue.
- **It hears the other person, not you.** Only system audio is captured, never the microphone.
  Deliberate.
- **Re-authorize after every update.** Without an Apple developer certificate, macOS treats each
  new build as a different app.
- **English → Chinese is the only pair actually tested.** French, German, Japanese and Spanish
  are selectable but unverified.
- **Compliance is on you.** Rules about disclosing that a meeting is being transcribed differ by
  country. The app records no audio and keeps no files, but whether you need to tell the other
  party is yours to check.

## Build from source

```bash
xcodegen generate && open AwesomeRealtimeTranslator.xcodeproj
```

```bash
xcodebuild -project AwesomeRealtimeTranslator.xcodeproj -scheme AwesomeRealtimeTranslator -destination 'platform=macOS' test
```

The project is generated from `project.yml` (XcodeGen); the `.xcodeproj` is committed too, so
you can open it without installing XcodeGen. Requirements: macOS 26.0+, Xcode 26+.

```
Sources/AwesomeRealtimeTranslator/
├── App/           pipeline assembly, menu bar, global hotkeys, app entry point
├── Audio/         system audio capture and PCM conversion
├── Transcription/ speech recognition
├── Segmentation/  sentence splitting (pure logic, unit-tested)
├── Translation/   on-device translation
├── Caption/       the floating caption panel
├── Permissions/   permission checks and onboarding
└── Settings/      settings and optional transcript archiving
```

[**Engineering notes →**](docs/ENGINEERING.md) Measured latency numbers, the authorization
pitfalls that will otherwise cost you a day, the anti-flicker work, deviations from the PRD,
and how the privacy claims are enforced in code.

## What is verified, and what is not

An honest dividing line. Do not read the two lists as one.

**Verified**

- Compiles clean under Swift 6 strict concurrency.
- All 27 sentence-splitting unit tests pass, including a full end-to-end event script. Three of
  them caught real bugs.
- Release build packaged as a `.app`: stays alive, no crash reports.
- The whole pipeline on a real machine — capture, recognition, splitting, translation, caption
  panel — with no errors in the logs across repeated runs.

**Not verified (needs a real session on your machine)**

1. Accuracy and latency against real speakers. The ~1.5s above was measured with synthesized
   speech, which has unnaturally even pacing.
2. The download path for a missing translation language pack.
3. Stability and CPU use over 60 continuous minutes.
4. Recovery after unplugging headphones or switching output devices.
5. Recognition quality with Chinese as the **source** language.
6. Whether the global hotkeys work without Input Monitoring access.

## Windows port

Written and feature-matched to the macOS version; the code lives in
[`windows/`](windows/README.md).

**[Download the beta (389 MB)](https://github.com/yangli-0409/Awesome-Realtime-Translator/releases/tag/v0.1.0-windows-beta)** · needs Windows 10 2004 or newer

It is labelled beta because **nobody has actually run it on Windows yet**. The port was
written on a Mac: it compiles to real Windows executables (verified for both x64 and arm64),
all 83 unit tests pass, and translation was measured end to end on the dev machine with the
real model. What is missing is the part that matters most — whether it captures system audio,
and whether the caption window really disappears from screen sharing.

**Check it yourself with your meeting app's share preview before you share a screen.**

| | macOS | Windows |
|---|---|---|
| System audio | ScreenCaptureKit | WASAPI loopback |
| Permissions needed | Screen & System Audio Recording | **none** |
| Speech | system SpeechAnalyzer | bundled whisper.cpp |
| Translation | system Translation framework | bundled OPUS-MT ONNX |
| Package size | 0.8 MB | about 540 MB |

The size difference is the whole story: Windows ships no on-device speech or translation,
so the models have to come along.

## Scope

`M1 prototype` (F1–F6) plus F7/F8/F9, which came almost for free. **Not done**: the session
history window, in-app purchase and trial limits, the onboarding step that plays sample audio,
and a localized UI.

## License

[MIT](LICENSE).
