# Contributing to Meetco

Thanks for helping make Meetco better! This guide explains how to set up the project and how we work.

## Getting started

1. Install Xcode 26 or newer (macOS 14+ deployment target, Swift 6).
2. Clone the repository and open `Meetco.xcodeproj`, or use the terminal:
   ```bash
   make test   # run unit tests
   make run    # build (Release) and launch
   ```
3. Add your own OpenAI API key in the app's onboarding window. **Never commit API keys** — the key lives only in your macOS Keychain, and Meetco does not read keys from `.env` files or environment variables.
4. Run `make hooks` once. It installs a pre-commit hook that blocks commits containing anything that looks like a key or token. You can also run `make check-secrets` at any time.

## Project conventions

- **Architecture:** MVVM-ish with dependency injection via `AppEnvironment`. Views read `@Observable` models from the SwiftUI environment and contain no business logic.
- **Services are protocol-based** (`TranscriptionServicing`, `MeetingAssisting`, `OpenAIClientProtocol`, `APIKeyStoring`, `ScreenContextCapturing`) so they can be faked in tests.
- **Concurrency:** Swift 6 strict concurrency. UI state is `@MainActor`; audio callbacks use locks or actors (`ChunkingProcessor`). Long-running work must be cancellable.
- **Keep files small and focused.** If a file grows past ~300 lines, split it.
- **Privacy:** never log API keys, request headers or transcript text. Use the `Log` loggers and mark only non-sensitive values `privacy: .public`.
- **Design:** native materials, monochrome icons, restrained animation. No bright colors or gradients in the UI.
- The Xcode project uses *file-system synchronized groups*: new files in `Meetco/` or `MeetcoTests/` are picked up automatically, so you never need to edit `project.pbxproj` to add a file.

## Pull requests

1. Create a feature branch.
2. Make sure `make test` and `make check-secrets` pass and the app builds with no warnings.
3. **Update `README.md`** whenever you add, change or remove behavior — the README must always describe the current implementation.
4. Describe what you changed and how you tested it (include the meeting platform/browser if relevant).

## Reporting security problems

Do not open a public issue. Follow [SECURITY.md](SECURITY.md).

## Reporting issues

Please include your macOS version, the meeting app (Meet in Chrome/Safari, Teams desktop/web), the audio source mode, and relevant logs from Console.app (filter by subsystem `io.github.meetco.Meetco`). Remove anything private before posting.
