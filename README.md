<p align="center">
  <img src="macos/Meetco/Resources/Assets.xcassets/AppIcon.appiconset/icon_128x128@2x.png" width="128" alt="Meetco icon">
</p>

<h1 align="center">Meetco</h1>

<p align="center">
  <b>Your AI companion for every conversation.</b><br>
  A native copilot for macOS and Windows. It listens to your call, spots the questions asked to you,<br>
  and types out an answer in your own words as soon as they are asked.
</p>

<p align="center">
  <a href="#download"><img alt="macOS 14+" src="https://img.shields.io/badge/macOS-14%2B-black?logo=apple"></a>
  <a href="#download"><img alt="Windows 10/11" src="https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows"></a>
  <img alt="Bring your own key" src="https://img.shields.io/badge/account-none%2C%20bring%20your%20own%20key-success">
  <img alt="License: MIT" src="https://img.shields.io/badge/license-MIT-blue">
</p>

---

## Download

<p align="center">
  <a href="https://github.com/efe-yeniyol/Meetco/releases/latest/download/Meetco-macOS.dmg"><img alt="Download for macOS" src="https://img.shields.io/badge/Download-macOS%20(.dmg)-black?style=for-the-badge&logo=apple"></a>
  &nbsp;
  <a href="https://github.com/efe-yeniyol/Meetco/releases/latest/download/Meetco-Windows-x64.zip"><img alt="Download for Windows" src="https://img.shields.io/badge/Download-Windows%20(x64)-0078D4?style=for-the-badge&logo=windows"></a>
</p>

| Platform | Download | Requirements |
|---|---|---|
| **macOS** (Apple silicon & Intel) | [**Meetco-macOS.dmg**](https://github.com/efe-yeniyol/Meetco/releases/latest/download/Meetco-macOS.dmg) · [.zip](https://github.com/efe-yeniyol/Meetco/releases/latest/download/Meetco-macOS.zip) | macOS 14 Sonoma or later |
| **Windows** (most PCs) | [**Meetco-Windows-x64.zip**](https://github.com/efe-yeniyol/Meetco/releases/latest/download/Meetco-Windows-x64.zip) | Windows 10 (2004+) or 11 |
| **Windows on ARM** (e.g. Snapdragon) | [**Meetco-Windows-arm64.zip**](https://github.com/efe-yeniyol/Meetco/releases/latest/download/Meetco-Windows-arm64.zip) | Windows 11 |

All versions and checksums: **[Releases](https://github.com/efe-yeniyol/Meetco/releases)**. You also need an
**[OpenAI API key](https://platform.openai.com/api-keys)** with billing enabled (a ChatGPT Plus
subscription does not include API access), or a key for another [supported provider](#models).

### Install

**macOS**
1. Open **Meetco-macOS.dmg** and drag **Meetco** onto the **Applications** folder.
2. Open Meetco from Applications. macOS will say it can't verify the developer, because the app is not notarized yet.
3. Go to **System Settings → Privacy & Security**, scroll down and click **Open Anyway**. You only do this once.

<details>
<summary>Prefer the terminal?</summary>

```sh
xattr -dr com.apple.quarantine /Applications/Meetco.app
```
</details>

**Windows**
1. Unzip **Meetco-Windows-x64.zip** (or arm64) anywhere, for example into `Documents\Meetco`.
2. Run **Meetco.exe**. In the SmartScreen dialog, click **More info → Run anyway**.

> **Windows is a preview.** It has the same features as the Mac app and passes its automated
> tests, but it has not been tried in many real meetings yet. Feedback is very welcome.

## What is Meetco?

Meetco is a small floating assistant that sits next to Google Meet, Microsoft Teams or any other
call. It is built for the meetings where you are expected to have answers ready: sales and customer
calls, demos, project and team meetings, and conversations in a second language.

- **Answers in your voice.** When someone asks you a question, Meetco writes the answer in the first person, ready to say out loud, in the speaker's language, with an optional Turkish or English translation.
- **No meeting bot.** Meetco never joins the call. It listens to your computer's audio output and, optionally, your microphone.
- **You stay in control.** Meetco never speaks, types or sends anything for you. You read the suggestion or copy it.
- **Bring your own key.** There is no Meetco account, server or subscription. All AI usage goes through your own OpenAI API key.
- **Native and lightweight.** SwiftUI on macOS, WPF on Windows. No Electron, no web views.

## Get started

1. **Download** and open Meetco for your platform (see above).
2. **Paste your OpenAI API key** in the welcome window. It is stored only in the macOS Keychain or the Windows Credential Manager.
3. **Allow audio access** when asked: on macOS, *Screen & System Audio Recording* and *Microphone*; on Windows, only the microphone.
4. Click **Listen**, then join your call. Use headphones so your microphone doesn't pick up the other participants.

Platform guides: **[macOS](macos/README.md)** · **[Windows](windows/README.md)**

## Features

**Answers**
- Detects questions automatically (English and Turkish) and starts answering as soon as one ends.
- Writes in your voice: first person, natural spoken language, and the real answer rather than an acknowledgement.
- Uses your **About you** notes (role, company, experience) as your own background, and does not make up personal facts, prices or commitments.
- The answer appears word by word, starting from the model's first token.
- On demand: **What should I say?**, **Recap so far**, **Questions to ask**, or answer any earlier question from the transcript.
- Copy, regenerate, dismiss, browse earlier answers, and attach a screenshot as context.

**Listening and transcription**
- Captures system audio and your microphone, with three modes: Mic + System, System only, Mic only.
- Live transcription over OpenAI's Realtime API, with an automatic fallback to file uploads.
- On-device voice activity detection, so silence is never sent.
- Chronological transcript labeled "Participants" / "You".

**Interface**
- A floating, always-on-top toolbar and panel that never steal focus from your meeting app.
- Global shortcuts, first-launch onboarding and a full Settings window.
- Optional: keep Meetco out of your screen shares, and keep a local transcript history (both off by default).

## How it works

```
 Meeting audio ──► Voice activity detection ──► Live transcription ──► Question detection
 (system + mic)    (on-device, drops silence)    (OpenAI Realtime)      (on-device)
                                                                              │
          Floating panel ◄── Answer typed out in your voice ◄── OpenAI chat model
```

1. Meetco detects speech on your computer and streams only the speech to OpenAI while it is being spoken.
2. When the speaker pauses (about 0.35 s), the phrase is committed and its transcript comes back.
3. If a participant's phrase looks like a question, Meetco asks the chat model for an answer straight away.
4. The answer streams back as plain text and appears on screen as it is written.

More detail: [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md).

## Models

You can change the models in **Settings → OpenAI** and type any model ID your account can use.

Answers can also come from **Claude (Anthropic)**, **DeepSeek**, a **local model** via Ollama or
LM Studio, or any OpenAI-compatible server (OpenRouter, Groq, Mistral, vLLM…). Pick one in
**Settings → Assistant**.

Transcription can run **on your computer** (Settings → Audio): free, private, no OpenAI key needed.
macOS 26 and later uses Apple's speech model; Windows uses Whisper (a one-time model download).
Pair it with Ollama or LM Studio and nothing leaves your computer.

| Role | Default | Alternatives |
|---|---|---|
| Answers | `gpt-6.1-terra` (reasoning effort `none`) | `gpt-6-luna` (fastest), `gpt-6.1-sol` and `gpt-6-astra` (smarter, but slower to start) |
| Transcription | `gpt-live-transcribe` (live streaming) | `gpt-transcribe` (uploads each phrase; also the automatic fallback) |

For the best speed, keep the default models and the **Fast** latency profile, and turn the
explanation language **Off** if you don't need a translation.

## Privacy and security

- **Your API key stays in your operating system's secure store** (macOS Keychain or Windows Credential Manager). It never appears in files, logs or this repository.
- **Audio is never written to disk.**
- **Speech and transcript excerpts are sent to OpenAI** using your key. Voice activity detection and question detection run on your computer.
- **No account, backend, analytics or telemetry.**
- Screenshots are taken only when you press the camera button.

Details: [docs/PRIVACY.md](docs/PRIVACY.md) · Reporting a vulnerability: [SECURITY.md](SECURITY.md)

## Responsible use

Meetco is a note-taking and preparation aid for meetings you take part in. Use it openly.

- **Tell people and get consent.** Recording or transcribing a conversation may require the consent of everyone in it, depending on where you live. Let participants know you use an AI assistant, and follow your organization's policies.
- **Respect the rules of the setting.** Don't use Meetco where outside help is not allowed, such as exams, assessments or job interviews, unless the organizer has explicitly said it is fine.
- **Check what you say.** Suggestions are generated by an AI model and can be wrong. You are responsible for what you say.

## Build from source

**macOS** (Xcode 26):

```bash
git clone https://github.com/efe-yeniyol/Meetco.git
cd Meetco
make install          # build and install to /Applications
make test             # unit tests
```

**Windows** ([.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)):

```powershell
git clone https://github.com/efe-yeniyol/Meetco.git
cd Meetco\windows
dotnet run --project src/Meetco.App            # run
dotnet test tests/Meetco.Core.Tests            # unit tests (also run on macOS/Linux)
```

Run `make help` to see every command.

## Repository layout

```
Meetco/
├── macos/          macOS app (Swift, SwiftUI, Xcode project)          → macos/README.md
├── windows/        Windows app (C#, .NET 10, WPF) and shared core     → windows/README.md
├── docs/           Architecture and privacy documentation
├── scripts/        Repository tools (secret scanning)
├── .github/        CI and release workflows
├── Makefile        Shortcuts for both apps (`make help`)
├── CONTRIBUTING.md
├── SECURITY.md
└── LICENSE
```

Both apps follow the same design and use the same prompts, so a change to behavior in one should
be mirrored in the other ([how](docs/ARCHITECTURE.md#keeping-the-two-apps-in-sync)).

## Contributing

Contributions are welcome. Please read [CONTRIBUTING.md](CONTRIBUTING.md) first. To report a
security problem, follow [SECURITY.md](SECURITY.md) instead of opening a public issue.

## License and disclaimer

Meetco is released under the [MIT License](LICENSE).

Meetco is an independent open-source project. It is not affiliated with, endorsed by, or sponsored by OpenAI, Google or Microsoft. "OpenAI", "Google Meet" and "Microsoft Teams" are trademarks of their respective owners. AI suggestions can be wrong, so check them before you rely on them. You are responsible for how you use Meetco and for complying with recording laws and the policies of your organization and the meetings you join.
