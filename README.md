<p align="center">
  <img src="Meetco/Resources/Assets.xcassets/AppIcon.appiconset/icon_128x128@2x.png" width="128" alt="Meetco icon">
</p>

<h1 align="center">Meetco</h1>

<p align="center">
  <b>Your AI companion for every conversation.</b><br>
  A native macOS meeting copilot that listens to your call, spots the questions asked to you,<br>
  and types out an answer in your own words while the other person is still finishing their sentence.
</p>

<p align="center">
  <img alt="macOS 14+" src="https://img.shields.io/badge/macOS-14%2B-black?logo=apple">
  <img alt="Swift 6" src="https://img.shields.io/badge/Swift-6-F05138?logo=swift&logoColor=white">
  <img alt="No dependencies" src="https://img.shields.io/badge/dependencies-none-success">
  <img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue">
</p>

---

## Contents

- [What is Meetco?](#what-is-meetco)
- [Features](#features)
- [How it works](#how-it-works)
- [Requirements](#requirements)
- [Quick start](#quick-start)
- [Setting up your OpenAI API key](#setting-up-your-openai-api-key)
- [macOS permissions](#macos-permissions)
- [Using Meetco in a meeting](#using-meetco-in-a-meeting)
- [Models and latency](#models-and-latency)
- [Privacy and security](#privacy-and-security)
- [Troubleshooting](#troubleshooting)
- [Development](#development)
- [Contributing](#contributing)
- [License and disclaimer](#license-and-disclaimer)

## What is Meetco?

Meetco is a small floating assistant that sits next to Google Meet, Microsoft Teams or any other call on your Mac. It listens to the meeting audio, transcribes it live, notices when someone asks **you** a question, and writes the answer **as you**, in the first person and ready to say out loud. It writes in the speaker's language and can add an optional translation in Turkish or English.

- **No meeting bot.** Meetco never joins the call. It listens to your Mac's audio output and, optionally, your microphone.
- **You stay in control.** Meetco never speaks, types or sends anything for you. You read the suggestion or copy it.
- **Bring your own key.** There is no Meetco account, server or subscription. All AI usage goes through your own OpenAI API key.
- **Native and lightweight.** Swift 6, SwiftUI, AppKit, ScreenCaptureKit and AVFoundation, with no Electron, no web views and no third-party dependencies.

## Features

**Answers**
- Detects questions automatically (English and Turkish) and starts answering as soon as one ends.
- Writes in your voice: first person, natural spoken language, with no "You could say…" and no filler. It adapts to how you speak in your own lines of the transcript.
- Gives the real answer, not just an acknowledgement. Asked "Do you know a crepe recipe?", it gives the ingredients and the steps.
- Uses your **About you** notes (role, company, experience) as your own background, and does not make up personal facts, prices or commitments.
- The answer is typed out character by character, starting from the model's first token.
- On demand: **What should I say?**, **Recap so far**, **Questions to ask**, or answer any earlier question from the transcript.
- Copy, regenerate, dismiss, and browse earlier answers. You can attach a screenshot as context for the next answer.

**Listening and transcription**
- Captures system audio with ScreenCaptureKit (excluding Meetco's own audio) and your microphone with AVAudioEngine, with input-device selection and automatic recovery when devices change.
- Three audio modes: Mic + System, System only, Mic only. You can switch them while listening.
- Live transcription over OpenAI's Realtime API, with an automatic fallback to file uploads.
- On-device voice activity detection, so silence is never sent.
- Chronological transcript labeled "Participants" / "You", with language badges.

**Interface**
- A floating, always-on-top glass toolbar and panel that never steal focus. Liquid Glass on macOS 26, vibrancy on macOS 14–15.
- Global shortcuts (⌃⌥ + key), first-launch onboarding, and a full Settings window.
- Optional "hide from screen sharing" (best effort).
- Optional local transcript history (off by default).

**Not implemented yet:** telling individual remote speakers apart (all remote audio is "Participants"), and custom shortcut keys.

## How it works

```
 Meeting audio ──► Voice activity detection ──► Live transcription ──► Question detection
 (system + mic)    (on-device, drops silence)    (OpenAI Realtime)      (on-device)
                                                                              │
          Floating panel ◄── Answer typed out in your voice ◄── OpenAI chat model
```

1. Meetco detects speech on your Mac and streams only the speech to OpenAI while it is being spoken.
2. When the speaker pauses (about 0.35 s), the phrase is committed and its transcript comes back.
3. If a participant's phrase looks like a question, Meetco asks the chat model for an answer straight away. If the question continues in the next phrase, the answer restarts with the full question.
4. The answer streams back as plain text and appears on screen as it is written.

## Requirements

- macOS **14 Sonoma** or later, on Apple silicon or Intel
- **Xcode 26** or later to build (Liquid Glass APIs need the macOS 26 SDK). Run `xcode-select --install` if `xcodebuild` is missing.
- An **OpenAI API account with billing enabled**. A ChatGPT Plus subscription does **not** include API access.

No CocoaPods, Swift packages or Homebrew packages are needed.

## Quick start

```bash
git clone https://github.com/efe-yeniyol/Meetco.git
cd Meetco
make install        # builds a Release version, copies it to /Applications and opens it
```

On first launch, a welcome window asks for your API key and the macOS permissions. After that, the toolbar appears at the top of the screen and the panel on the right. Click **Listen**, then join your call.

### All build commands

| Command | What it does |
|---|---|
| `make install` | Build Release and install to `/Applications` (recommended) |
| `make run` | Build Release into `build/Meetco.app` and launch it |
| `make build` / `make debug` | Build only |
| `make test` | Run the unit tests |
| `make open` | Open the project in Xcode |
| `make check-secrets` | Make sure no credentials would be published |
| `make hooks` | Install a pre-commit hook that blocks committed secrets |
| `make clean` | Remove build output |

To use Xcode instead, open `Meetco.xcodeproj`, select the **Meetco** scheme and **My Mac**, then press **⌘R**.

### Code signing and permissions

macOS ties privacy permissions to the app's location and **code signature**, so installing to `/Applications` is recommended.

- If an **Apple Development** certificate is in your keychain, `scripts/build.sh` signs with it automatically, and your permissions survive rebuilds. A free Apple Account signed in under Xcode → Settings → Accounts provides one.
- Without a certificate, the app is ad-hoc signed and macOS treats every rebuild as a new app, so you have to grant permissions again after each rebuild.
- The chosen certificate is remembered in `.signing-identity`. This file is git-ignored and never committed.
- To pick a certificate: `MEETCO_SIGN_IDENTITY="Apple Development: Your Name (TEAMID)" make install`. To force ad-hoc signing: `MEETCO_SIGN_IDENTITY=- make install`.

## Setting up your OpenAI API key

1. Create a key at <https://platform.openai.com/api-keys>.
2. Paste it in Meetco's welcome window or in **Settings (⌘,) → OpenAI**.
3. Click **Save API Key**, then **Test Connection**.
4. Optional: fill in **About you** with your role, company, product facts and experience. Meetco speaks from these as if they were your own memories.

**Where your key is stored:** only in your **macOS login Keychain**. Meetco does not read keys from environment variables, `.env` files or config files, so there is nothing to set up in the repository and nothing that can be committed by accident. Use **Delete Key** in Settings to remove it. Meetco makes no API calls until a key is saved.

## macOS permissions

| Permission | Why it's needed | Where to enable it |
|---|---|---|
| **Screen & System Audio Recording** | ScreenCaptureKit needs it to deliver meeting audio. Meetco requests audio only (and the optional screenshot button). | System Settings → Privacy & Security → **Screen & System Audio Recording** (the top list), then **relaunch Meetco** |
| **Microphone** | To transcribe your own voice in "Mic + System" and "Mic only" modes | System Settings → Privacy & Security → **Microphone** |

macOS shows the permission dialog only once. After that, **Grant…** in Meetco opens System Settings, and a **Relaunch Meetco** button appears, because macOS applies screen recording access only after a restart. On macOS 15 and later, the system may occasionally ask you to confirm continued access. Choose **Allow**.

## Using Meetco in a meeting

Meetco works with **Google Meet** (Chrome, Safari or any other browser) and **Microsoft Teams** (desktop or web), and with any other app whose audio plays through your Mac. It does not integrate with these services directly.

1. Click **Listen** in the toolbar, or press **⌃⌥L**.
2. Join the call as usual.
3. **Use headphones** with the default **Mic + System** mode. Otherwise your microphone picks up the other participants a second time.
4. When someone asks you something, the question appears in the panel and the answer is typed out under it.
5. Press **What should I say?** (⌃⌥A) at any time. Recap (⌃⌥R) and Questions to ask are in the tools sidebar.
6. Click **Stop** when the meeting ends.

If a meeting is in full screen, Meetco stays on top as long as **Show on all Spaces and over full-screen meetings** is on (Settings → General).

### Shortcuts and controls

| Toolbar | Shortcut | Action |
|---|---|---|
| ● Listen / timer | ⌃⌥L | Start or stop listening. Right-click to choose the audio source. |
| 🎙 | | Mute or unmute your microphone |
| 💬 | ⌃⌥A | What should I say? |
| | ⌃⌥R | Recap so far |
| 📷 | | Use a screenshot as context for the next answer |
| 👁 | ⌃⌥M | Show or hide the panel |
| | ⌃⌥C | Toggle the compact panel size |
| | ⌃⌥H | Show or hide all of Meetco |
| ⚙︎ | ⌘, | Settings |

Hover over an answer for **Copy**, **Regenerate**, **Dismiss** and **‹ ›** (browse earlier answers). The line at the bottom of the panel shows the latest speech heard and an input level, so you can always tell that Meetco is listening. Click it to open the full transcript.

## Models and latency

Meetco is tuned for speed first. You can change the models in **Settings → OpenAI**, and you can type any model ID your account can use.

| Role | Default | Alternatives |
|---|---|---|
| Answers | `gpt-6.1-terra` with reasoning effort `none` | `gpt-6-luna` (fastest), `gpt-6.1-sol` and `gpt-6-astra` (smarter, but they always reason first, so answers start later) |
| Transcription | `gpt-live-transcribe` (live streaming) | `gpt-transcribe` (uploads each phrase after it ends; also used automatically as the fallback) |

The `gpt-4o-*-transcribe` models and `whisper-1` are deprecated by OpenAI and shut down on **February 26, 2027**. Settings that still use the old defaults are upgraded automatically.

**Where the time goes**, from the moment the speaker stops talking:

1. About **0.35 s** of silence ends the phrase (the **Fast** latency profile, which is the default).
2. The transcript arrives shortly after, because the model heard the audio while it was being spoken. If the live connection is unavailable, the phrase is uploaded instead, which adds roughly 0.5–1 s.
3. A phrase ending in "?" is answered immediately. Other questions wait **0.25 s** in case the speaker continues.
4. The first answer words appear as soon as the model writes them.

Answer latency has not been measured systematically yet. Reports with your network and region are welcome.

**For the best speed:** keep the default models, keep **Latency** on **Fast** (Settings → Audio), use a wired or strong connection, and turn the explanation language **Off** if you don't need a translation.

## Privacy and security

- **Your API key stays in the macOS Keychain.** It never appears in files, UserDefaults, logs or this repository.
- **Audio is never written to disk.** It is kept in memory only.
- **Speech and transcript excerpts are sent to OpenAI** (or to the base URL you configure) using your key. **Meetco does not process everything locally.** Voice activity detection, language identification and question detection run on your Mac.
- There is **no account, backend, analytics or telemetry**.
- Screenshots are taken only when you press the camera button, and are sent only once.
- Transcripts are saved locally only if you turn that on (Settings → Privacy), and you can delete them there.
- A pulsing red dot and the macOS recording indicator show when Meetco is listening.

**Get consent.** Recording or transcribing a conversation may require the consent of everyone in it, depending on where you live and your organization's rules. You are responsible for informing participants.

More details:
- [docs/PRIVACY.md](docs/PRIVACY.md): exactly what leaves your Mac, where it goes and when
- [SECURITY.md](SECURITY.md): how to report a vulnerability, and how credentials are handled

## Troubleshooting

| Problem | Fix |
|---|---|
| "Screen & System Audio Recording permission is required" | Enable Meetco in System Settings → Privacy & Security → Screen & System Audio Recording, then **quit and relaunch** Meetco. |
| macOS keeps asking for the permission even though Meetco is enabled | Relaunch Meetco first. If that doesn't help, older ad-hoc builds may have left stale entries: quit Meetco, run `tccutil reset ScreenCapture io.github.meetco.Meetco && tccutil reset Microphone io.github.meetco.Meetco`, then `make install`, enable Meetco again and relaunch. |
| Permissions disappear after rebuilding | The build was ad-hoc signed (the build prints `🔏 Signing ad-hoc`). Add an Apple Development certificate (Xcode → Settings → Accounts) and run `make install` again. |
| macOS asks for Keychain access after a rebuild | Same cause. Click **Always Allow**, or enter the key again in Settings. |
| Nothing is transcribed | Check the level meter at the bottom of the panel. Make sure the meeting audio actually plays through the Mac, and that **Test Connection** succeeds. |
| Transcripts are slower than expected | The live connection may be failing, in which case Meetco silently falls back to file uploads. In Console.app, look for `Live transcription connected` under the `transcription` category. |
| Participants appear twice in the transcript | Your microphone is picking up your speakers. Use headphones, or switch to **System Audio Only**. |
| "OpenAI rejected the API key (401)" | The key is wrong or revoked. Paste a new key and test again. |
| "Your OpenAI account has no available credit" | Add billing or credits at platform.openai.com. |
| "model not found" / 404 | Your account doesn't have access to that model. Choose another one in Settings → OpenAI. |
| Answers are slow | Use `gpt-6.1-terra` or `gpt-6-luna`, keep Latency on **Fast**, and keep transcription on `gpt-live-transcribe`. |
| Meetco shows up in my screen share | Hiding from screen sharing is best effort, and some capture tools ignore it. Hide Meetco with ⌃⌥H before presenting. |
| Global shortcuts don't work | Another app may use the same combination. Check Settings → Shortcuts. |
| Windows are off-screen | Settings → General → **Reset Window Positions**, then relaunch. |

**Logs:** open **Console.app** and filter by the subsystem `io.github.meetco.Meetco`. Logs never contain your API key or transcript text. Remove anything private before sharing logs in an issue.

## Development

### Architecture

MVVM with a small dependency-injection container (`AppEnvironment`). Views read `@Observable` models, and services are protocol-based so they can be replaced with fakes in tests.

```
Audio (ScreenCaptureKit / AVAudioEngine)
   │  16 kHz mono Float32 via AsyncStream
   ▼
ChunkingProcessor (actor) → AudioChunker (voice activity detection)
   │  ├─ speech is streamed live ──► RealtimeTranscriptionSession (WebSocket, 24 kHz PCM16)
   │  └─ AudioChunk at each pause (commit)
   ▼
TranscriptionCoordinator → LiveFirstTranscriptionService
   │                         ├─ live transcript for the committed phrase
   │                         └─ fallback: OpenAITranscriptionService (file upload)
   │  filter hallucinations, detect language & questions, de-duplicate
   ▼
TranscriptStore  ──►  SuggestionEngine (trigger, merge continuations, dedupe, cancel)
                          │
                          ▼
              OpenAIMeetingAssistant → Chat Completions (streamed)
              (plain-text spoken answers; JSON schema for recaps and question lists)
                          │
                          ▼
              Floating panel (typewriter rendering)
```

### Project layout

```
Meetco/
  App/                    App entry point, AppDelegate, AppEnvironment (DI), AppState, AppActions
  Core/
    Models/               AudioSource, TranscriptSegment, Suggestion, Preferences + model catalog
    Protocols/            Service protocols (transcription, assistant, key storage, screen capture)
    Utilities/            Logging, language detection, text similarity, WAV encoding, JSON helpers
    DesignSystem/         Theme tokens, glass containers, shared components
  Features/
    AudioCapture/         System audio & microphone capture, resampling, chunking, permissions
    Session/              MeetingSessionController (owns the listening lifecycle)
    Transcription/        Live (Realtime) and file transcription, transcript store and view
    MeetingIntelligence/  Question detection, prompts, response parsing, suggestion engine
    Suggestions/          Live answer view (typewriter), suggestion cards
    FloatingPanel/        NSPanel windows, toolbar, panel and sidebar views
    Settings/             Settings store and tabs, onboarding, API key UI
    ScreenContext/        Optional screenshot capture
    Hotkeys/              Global shortcuts (Carbon)
  Infrastructure/
    OpenAI/               URLSession-based OpenAI client, request/response models, errors
    Storage/              Keychain, API key cache, optional local session archive
  Resources/              Asset catalog (app icon)
MeetcoTests/              Unit tests (XCTest)
Config/                   Entitlements (hardened runtime + audio input)
scripts/                  build, run, install, icon generation, secret scanning
docs/                     Privacy documentation
```

### Tests

```bash
make test
```

The unit tests cover voice activity detection and chunking, live audio streaming and the Realtime wire format, question detection (including interview phrasing), plain-text and JSON answer parsing, streaming partial answers, prompts and schemas, model parameter fallbacks, WAV encoding and transcript filters. The tests make no network calls and need no API key.

The app has been built and its UI checked on macOS 26.5. It has **not** yet been tested end to end in a live Google Meet or Teams call with a real API key. Please report what works for you.

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first. In short:

1. Run `make hooks` once, so commits containing anything that looks like a key are blocked.
2. Keep `make test` passing and the build free of warnings.
3. Update this README whenever you change behavior.
4. Never include API keys, transcripts or other private data in code, issues, logs or screenshots.

To report a security problem, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## License and disclaimer

Meetco is released under the [MIT License](LICENSE).

Meetco is an independent open-source project. It is not affiliated with, endorsed by, or sponsored by OpenAI, Google or Microsoft. "OpenAI", "Google Meet" and "Microsoft Teams" are trademarks of their respective owners. AI suggestions can be wrong, so check them before you rely on them. You are responsible for how you use Meetco and for complying with recording laws and the policies of your organization and the meetings you join.
