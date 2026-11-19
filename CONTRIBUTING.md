# Contributing to Meetco

Thanks for helping make Meetco better! This guide explains how to set up the project and how we work.

## Repository layout

| Folder | What | Toolchain |
|---|---|---|
| `macos/` | macOS app (Swift 6, SwiftUI, AppKit) | Xcode 26+ |
| `windows/` | Windows app (C#, .NET 10, WPF) and its platform-independent core | .NET 10 SDK |
| `docs/` | Architecture and privacy documentation | |
| `scripts/` | Repository tools (secret scanning) | |

Run `make help` in the repository root to see the shortcuts for both apps.

## Getting started

**macOS**

```bash
make test      # unit tests
make run       # build (Release) and launch
```

or open `macos/Meetco.xcodeproj` in Xcode.

**Windows**

```powershell
cd windows
dotnet test tests/Meetco.Core.Tests     # core tests (also run on macOS and Linux)
dotnet run --project src/Meetco.App      # run the app (Windows only)
```

**Both**

1. Add your own OpenAI API key in the app's welcome window. **Never commit API keys.** The key lives only in the macOS Keychain or the Windows Credential Manager, and Meetco does not read keys from `.env` files or environment variables.
2. Run `make hooks` once. It installs a pre-commit hook that blocks commits containing anything that looks like a key or token. You can also run `make check-secrets` at any time.

## Project conventions

- **Keep the two apps in sync.** Prompts, question detection, timing thresholds, the model catalog and the wire formats must behave the same on both platforms. See [docs/ARCHITECTURE.md](docs/ARCHITECTURE.md#keeping-the-two-apps-in-sync).
- **Services are interface/protocol based** so they can be faked in tests.
- **macOS concurrency:** Swift 6 strict concurrency. UI state is `@MainActor`; audio callbacks use locks or actors. Long-running work must be cancellable.
- **Windows threading:** `Meetco.Core` observable classes are used on the UI thread only; background callbacks post back through `UiDispatcher`. Keep Windows APIs out of `Meetco.Core`.
- **Keep files small and focused.** If a file grows past ~300 lines, split it.
- **Privacy:** never log API keys, request headers or transcript text.
- **Design:** native look, monochrome icons, restrained animation.
- The Xcode project uses *file-system synchronized groups*: new files in `macos/Meetco/` or `macos/MeetcoTests/` are picked up automatically.

## Pull requests

1. Create a feature branch.
2. Make sure the tests for the app you changed pass (`make test`, `dotnet test`), `make check-secrets` passes, and the build has no warnings. CI runs the Windows build and tests on every pull request that touches `windows/`.
3. **Update the README** of the app you changed whenever you add, change or remove behavior.
4. Describe what you changed and how you tested it (include the OS, meeting app and audio mode if relevant).

## Releases

Pushing a tag like `v1.2.0` runs the **Release** workflow. It builds `Meetco-macOS.dmg`
(plus `Meetco-macOS.zip`), `Meetco-Windows-x64.zip` and `Meetco-Windows-arm64.zip`, stamps the tag
as the app version, and attaches them with `SHA256SUMS.txt` to a GitHub release.
`make dmg` builds the same DMG locally.
The download links in the main README always point to the latest release.

## Reporting security problems

Do not open a public issue. Follow [SECURITY.md](SECURITY.md).

## Reporting issues

Please include your OS version, the meeting app (for example Meet in Chrome, Teams desktop), the
audio source mode, and relevant logs (macOS: Console.app, subsystem `io.github.meetco.Meetco`;
Windows: `%LOCALAPPDATA%\Meetco\logs`). Remove anything private before posting.
