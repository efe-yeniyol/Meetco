# Meetco for macOS

The native macOS app: Swift 6, SwiftUI, AppKit, ScreenCaptureKit and AVFoundation, with no
third-party dependencies. For an overview of Meetco and download links, see the
[main README](../README.md).

## Contents

- [Requirements](#requirements)
- [Install a release](#install-a-release)
- [Build from source](#build-from-source)
- [API key](#api-key)
- [Permissions](#permissions)
- [Using Meetco in a meeting](#using-meetco-in-a-meeting)
- [Shortcuts and controls](#shortcuts-and-controls)
- [Troubleshooting](#troubleshooting)
- [Development](#development)

## Requirements

- macOS **14 Sonoma** or later, on Apple silicon or Intel
- To build: **Xcode 26** or later (Liquid Glass APIs need the macOS 26 SDK). Run `xcode-select --install` if `xcodebuild` is missing.
- An **OpenAI API account with billing enabled**. A ChatGPT Plus subscription does **not** include API access.

## Install a release

1. Download **[Meetco-macOS.zip](https://github.com/efe-yeniyol/Meetco/releases/latest/download/Meetco-macOS.zip)** and unzip it.
2. Move **Meetco.app** to **Applications**.
3. Release builds are not notarized yet, so macOS blocks the first launch. Open Meetco once, then go to
   **System Settings → Privacy & Security**, scroll down and click **Open Anyway**.

Building from source (below) avoids this step and keeps permissions stable across your own rebuilds.

## Build from source

```bash
git clone https://github.com/efe-yeniyol/Meetco.git
cd Meetco
make install        # builds a Release version, copies it to /Applications and opens it
```

Every command below works from the repository root (it forwards to `macos/`) or from inside `macos/`.

| Command | What it does |
|---|---|
| `make install` | Build Release and install to `/Applications` (recommended) |
| `make run` | Build Release into `macos/build/Meetco.app` and launch it |
| `make build` / `make -C macos debug` | Build only |
| `make test` | Run the unit tests |
| `make open` | Open the project in Xcode |
| `make clean` | Remove build output |

To use Xcode instead, open `macos/Meetco.xcodeproj`, select the **Meetco** scheme and **My Mac**, then press **⌘R**.

### Code signing and permissions

macOS ties privacy permissions to the app's location and **code signature**, so installing to `/Applications` is recommended.

- If an **Apple Development** certificate is in your keychain, `macos/scripts/build.sh` signs with it automatically, and your permissions survive rebuilds. A free Apple Account signed in under Xcode → Settings → Accounts provides one.
- Without a certificate, the app is ad-hoc signed and macOS treats every rebuild as a new app, so you have to grant permissions again after each rebuild.
- The chosen certificate is remembered in `macos/.signing-identity`. This file is git-ignored and never committed.
- To pick a certificate: `MEETCO_SIGN_IDENTITY="Apple Development: Your Name (TEAMID)" make install`. To force ad-hoc signing: `MEETCO_SIGN_IDENTITY=- make install`.

## API key

1. Create a key at <https://platform.openai.com/api-keys>.
2. Paste it in Meetco's welcome window or in **Settings (⌘,) → OpenAI**.
3. Click **Save API Key**, then **Test Connection**.
4. Optional: fill in **About you** with your role, company, product facts and experience. Meetco speaks from these as if they were your own memories.

The key is stored only in your **macOS login Keychain**. Meetco does not read keys from environment variables, `.env` files or config files. Use **Delete Key** in Settings to remove it. Meetco makes no API calls until a key is saved.

## Permissions

| Permission | Why it's needed | Where to enable it |
|---|---|---|
| **Screen & System Audio Recording** | ScreenCaptureKit needs it to deliver meeting audio. Meetco requests audio only (and the optional screenshot button). | System Settings → Privacy & Security → **Screen & System Audio Recording** (the top list), then **relaunch Meetco** |
| **Microphone** | To transcribe your own voice in "Mic + System" and "Mic only" modes | System Settings → Privacy & Security → **Microphone** |

macOS shows the permission dialog only once. After that, **Grant…** in Meetco opens System Settings, and a **Relaunch Meetco** button appears, because macOS applies screen recording access only after a restart. On macOS 15 and later, the system may occasionally ask you to confirm continued access. Choose **Allow**.

## Using Meetco in a meeting

Meetco works with **Google Meet** (any browser), **Microsoft Teams** (desktop or web), and any other app whose audio plays through your Mac. It does not integrate with these services directly.

1. Click **Listen** in the toolbar, or press **⌃⌥L**.
2. Join the call as usual.
3. **Use headphones** with the default **Mic + System** mode. Otherwise your microphone picks up the other participants a second time.
4. When someone asks you something, the question appears in the panel and the answer is typed out under it.
5. Press **What should I say?** (⌃⌥A) at any time. Recap (⌃⌥R) and Questions to ask are in the tools sidebar.
6. Click **Stop** when the meeting ends.

If a meeting is in full screen, Meetco stays on top as long as **Show on all Spaces and over full-screen meetings** is on (Settings → General).

## Shortcuts and controls

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
| Meetco shows up in my screen share | Turn on **Keep Meetco out of my screen shares when presenting** (Settings → General) or hide it with ⌃⌥H. The setting is best effort, and some capture tools ignore it. |
| Global shortcuts don't work | Another app may use the same combination. Check Settings → Shortcuts. |
| Windows are off-screen | Settings → General → **Reset Window Positions**, then relaunch. |

**Logs:** open **Console.app** and filter by the subsystem `io.github.meetco.Meetco`. Logs never contain your API key or transcript text. Remove anything private before sharing logs in an issue.

## Development

MVVM with a small dependency-injection container (`AppEnvironment`). Views read `@Observable` models, and services are protocol-based so they can be replaced with fakes in tests. The audio → transcript → answer pipeline is described in [docs/ARCHITECTURE.md](../docs/ARCHITECTURE.md).

```
macos/
├── Meetco/
│   ├── App/                    App entry point, AppDelegate, AppEnvironment (DI), AppState, AppActions
│   ├── Core/
│   │   ├── Models/             AudioSource, TranscriptSegment, Suggestion, Preferences + model catalog
│   │   ├── Protocols/          Service protocols (transcription, assistant, key storage, screen capture)
│   │   ├── Utilities/          Logging, language detection, text similarity, WAV encoding, JSON helpers
│   │   └── DesignSystem/       Theme tokens, glass containers, shared components
│   ├── Features/
│   │   ├── AudioCapture/       System audio & microphone capture, resampling, chunking, permissions
│   │   ├── Session/            MeetingSessionController (owns the listening lifecycle)
│   │   ├── Transcription/      Live (Realtime) and file transcription, transcript store and view
│   │   ├── MeetingIntelligence/ Question detection, prompts, response parsing, suggestion engine
│   │   ├── Suggestions/        Live answer view (typewriter), suggestion cards
│   │   ├── FloatingPanel/      NSPanel windows, toolbar, panel and sidebar views
│   │   ├── Settings/           Settings store and tabs, onboarding, API key UI
│   │   ├── ScreenContext/      Optional screenshot capture
│   │   └── Hotkeys/            Global shortcuts (Carbon)
│   ├── Infrastructure/
│   │   ├── OpenAI/             URLSession-based OpenAI client, request/response models, errors
│   │   └── Storage/            Keychain, API key cache, optional local session archive
│   └── Resources/              Asset catalog (app icon)
├── MeetcoTests/                Unit tests (XCTest)
├── Meetco.xcodeproj
├── Config/                     Entitlements (hardened runtime + audio input)
├── scripts/                    build, run, install, icon generation
└── Makefile
```

The Xcode project uses *file-system synchronized groups*: new files in `Meetco/` or `MeetcoTests/` are picked up automatically, so you never need to edit `project.pbxproj` to add a file.

### Tests

```bash
make test
```

The unit tests cover voice activity detection and chunking, live audio streaming and the Realtime wire format, question detection (including open-ended requests), plain-text and JSON answer parsing, streaming partial answers, prompts and schemas, model parameter fallbacks, WAV encoding and transcript filters. They make no network calls and need no API key.
