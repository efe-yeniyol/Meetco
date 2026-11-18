# Architecture

Meetco has two native apps that share one design: the macOS app in Swift (`macos/`) and the
Windows app in C# (`windows/`). They use the same pipeline, prompts, thresholds and OpenAI
wire formats, and their files and types have matching names.

## Pipeline

```
System audio + microphone                macOS: ScreenCaptureKit / AVAudioEngine
   │  16 kHz mono float32                Windows: WASAPI loopback / WASAPI capture (NAudio)
   ▼
ChunkingProcessor → AudioChunker         energy-based voice activity detection, per source
   │  ├─ speech is streamed live ──► RealtimeTranscriptionSession (WebSocket, 24 kHz PCM16)
   │  └─ AudioChunk at each pause (commit)
   ▼
TranscriptionCoordinator → LiveFirstTranscriptionService
   │                         ├─ live transcript for the committed phrase
   │                         └─ fallback: OpenAITranscriptionService (WAV upload)
   │  filter hallucinations, detect language and questions, de-duplicate
   ▼
TranscriptStore ──► SuggestionEngine     trigger, merge continuations, dedupe, cancel
                        │
                        ▼
            OpenAIMeetingAssistant → Chat Completions (streamed)
            (plain-text spoken answers; JSON schema for recaps and question lists)
                        │
                        ▼
            Floating panel (answer appears word by word)
```

`MeetingSessionController` owns the listening lifecycle: it starts and stops capture, wires each
source to its chunker and live transcription connection, flushes utterances when audio stops
arriving, and reconnects system audio after interruptions.

## Where things live

| Concern | macOS (`macos/Meetco/`) | Windows (`windows/src/`) |
|---|---|---|
| Composition root | `App/AppEnvironment.swift` | `Meetco.App/App.xaml.cs` |
| Voice activity detection | `Features/AudioCapture/Services/AudioChunker.swift` | `Meetco.Core/Audio/AudioChunker.cs` |
| Live transcription | `Features/Transcription/Services/RealtimeTranscription.swift` | `Meetco.Core/Transcription/RealtimeTranscription.cs`, `RealtimeWire.cs` |
| Question detection | `Features/MeetingIntelligence/Services/QuestionDetector.swift` | `Meetco.Core/Text/QuestionDetector.cs` |
| Prompts and schemas | `Features/MeetingIntelligence/Services/PromptBuilder.swift` | `Meetco.Core/Assistant/PromptBuilder.cs` |
| Answer parsing | `ResponseParser.swift`, `AssistantModels.swift` | `Meetco.Core/Assistant/ResponseParser.cs`, `AssistantModels.cs` |
| Suggestion engine | `Features/MeetingIntelligence/Services/SuggestionEngine.swift` | `Meetco.Core/Session/SuggestionEngine.cs` |
| Session lifecycle | `Features/Session/Services/MeetingSessionController.swift` | `Meetco.Core/Session/MeetingSessionController.cs` |
| OpenAI client | `Infrastructure/OpenAI/OpenAIClient.swift` (URLSession) | `Meetco.Core/OpenAI/OpenAIClient.cs` (HttpClient) |
| Model catalog | `Core/Models/Preferences.swift` | `Meetco.Core/Models/Preferences.cs` |
| API key storage | Keychain (`Infrastructure/Storage/KeychainManager.swift`) | Credential Manager (`Meetco.App/Platform/`) |
| Audio capture | `Features/AudioCapture/Services/` | `Meetco.App/Platform/` |
| UI | SwiftUI + AppKit panels | WPF windows |

On Windows, everything that does not touch the operating system is in `Meetco.Core`, a plain
`net10.0` library with no Windows dependencies. Its tests run on any machine with the .NET SDK.
Unlike the Swift app, the C# session controller receives audio sources through
`IAudioCaptureFactory`, so the whole capture → transcript → answer flow is unit-tested with fakes.

## Threading

- **macOS:** Swift 6 strict concurrency. UI state is `@MainActor @Observable`; audio chunking runs in actors.
- **Windows:** Core's observable classes are used on the UI thread only. Audio callbacks run on
  capture threads, chunk under a lock (`ChunkingProcessor`), and post results back through
  `UiDispatcher` (the WPF `SynchronizationContext`). The Realtime session runs its own loop.

## Keeping the two apps in sync

Behavior that the user notices must match on both platforms. When you change any of these in one
app, change the other in the same pull request:

- prompt text, JSON schemas and the plain-text answer format (`PromptBuilder`, `PlainAnswer`)
- question detection word lists and thresholds (`QuestionDetector`)
- voice activity and latency settings (`AudioChunkerConfiguration`, `LatencyProfile`)
- suggestion timing (`SuggestionEngine` debounce, continuation and duplicate thresholds)
- the model catalog and reasoning-effort rules (`ModelCatalog`)
- the Realtime and REST wire formats

Both test suites contain the same core cases (chunker, question detector, parser, partial JSON,
wire format), so a mismatch usually shows up as a failing test on one side.
