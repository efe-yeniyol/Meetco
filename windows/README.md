# Meetco for Windows (preview)

A native Windows port of Meetco. It works like the macOS app: it listens to your meeting audio,
transcribes it live, notices questions asked to you, and types out an answer in your own words.
It uses your own OpenAI API key; there is no Meetco account or server.

The macOS app (`../Meetco`) is untouched. The Windows app lives entirely in this folder.

## Requirements

- Windows 10 (version 2004 or later) or Windows 11, x64 or ARM64
- An OpenAI API account with billing enabled
- To build from source: the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)

## Download

- **[Meetco-Windows-x64.zip](https://github.com/efe-yeniyol/Meetco/releases/latest/download/Meetco-Windows-x64.zip)** for most PCs
- **[Meetco-Windows-arm64.zip](https://github.com/efe-yeniyol/Meetco/releases/latest/download/Meetco-Windows-arm64.zip)** for Windows on ARM (e.g. Snapdragon)

Unzip it anywhere (for example `C:\Users\<you>\Apps\Meetco`) and run `Meetco.exe`. The app is
self-contained: no .NET install is needed.

The app is not code-signed yet, so Windows SmartScreen may warn on first launch.
Choose **More info → Run anyway**.

Development builds: every push to `main` that changes `windows/` runs the **Windows** workflow;
its `Meetco-win-x64` / `Meetco-win-arm64` artifacts are under **Actions → Windows**.

## Build from source

```powershell
cd windows
dotnet test tests/Meetco.Core.Tests          # unit tests (also run on macOS/Linux)
dotnet run --project src/Meetco.App          # run the app (Windows only)

# Self-contained single-file build in publish/win-x64
dotnet publish src/Meetco.App -c Release -r win-x64 --self-contained -p:PublishSingleFile=true -o publish/win-x64
```

## First launch

1. Paste your OpenAI API key in the welcome window and click **Save**. The key is stored only in
   **Windows Credential Manager** (generic credential `Meetco/OpenAI-API-Key`). Meetco never reads
   keys from files or environment variables.
2. Microphone access: **Settings → Privacy & security → Microphone →
   Let desktop apps access your microphone** must be on.
3. System audio needs no permission on Windows. Meetco records what your default output device
   plays (WASAPI loopback), so join the call on the same device you hear it on.
4. Click **Listen**, then join your call.

## Shortcuts

All shortcuts are global and use **Ctrl + Alt**:

| Keys | Action |
|---|---|
| Ctrl+Alt+L | Start / stop listening |
| Ctrl+Alt+A | What should I say? |
| Ctrl+Alt+R | Recap so far |
| Ctrl+Alt+M | Show / hide panel |
| Ctrl+Alt+C | Toggle compact mode |
| Ctrl+Alt+H | Show / hide Meetco |

## Where things are stored

| What | Where |
|---|---|
| API key | Windows Credential Manager |
| Settings | `%APPDATA%\Meetco\settings.json` |
| Transcript history (off by default) | `%APPDATA%\Meetco\Sessions` |
| Logs (no keys, no transcripts) | `%LOCALAPPDATA%\Meetco\logs` |

Audio is never written to disk.

## Project layout

```
windows/
├── src/Meetco.Core/          Platform-independent logic (net10.0): voice activity detection,
│                             live + file transcription, question detection, prompts, OpenAI client,
│                             suggestion engine, session controller. Mirrors the Swift code.
├── src/Meetco.App/           WPF app (net10.0-windows): WASAPI capture (NAudio), floating windows,
│                             Credential Manager, global hotkeys, screenshots, settings UI.
└── tests/Meetco.Core.Tests/  xUnit tests (ports of the Swift tests plus session tests).
```

`Meetco.Core` has no Windows dependencies, so its tests run anywhere the .NET SDK runs.
When you change prompts, thresholds or the wire format in the Swift app, mirror the change in
`Meetco.Core` (the file and type names match the Swift ones).

## Differences from the macOS app

- System audio is captured with WASAPI loopback instead of ScreenCaptureKit. Every sound the
  default output device plays is captured, including notification sounds.
- Language detection for transcript badges is a lightweight English/Turkish heuristic
  (macOS uses Apple's NaturalLanguage framework). The OpenAI transcription itself is unchanged.
- "Hide from screen sharing" uses `WDA_EXCLUDEFROMCAPTURE`, which most screen-sharing apps on
  Windows 10 2004+ respect.

## Responsible use

Meetco is meant to be used openly, with the knowledge of the people you meet. See the main
[README](../README.md#responsible-use).
