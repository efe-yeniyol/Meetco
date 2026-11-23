# Meetco Privacy & Data Handling

Meetco is a Bring-Your-Own-Key (BYOK) app for macOS and Windows. It has no Meetco account, no
backend servers, no analytics and no telemetry. Both apps handle data the same way.

## What leaves your computer

| Data | Sent to | When |
|------|---------|------|
| Speech audio, streamed while someone is talking (24 kHz mono PCM) | OpenAI Realtime API (`wss://…/v1/realtime?intent=transcription`) | While listening, with the default `gpt-live-transcribe` model. Not sent when on-device transcription is chosen. Only detected speech is streamed; silence is not sent, and audio judged to be noise is discarded from the stream. |
| Short speech audio clips (16 kHz mono WAV, a few seconds each) | OpenAI `/v1/audio/transcriptions` | With a file-based transcription model, or as a fallback when the live connection is unavailable. Silence and very short noises are not sent. Not sent with on-device transcription. |
| Recent transcript excerpts, your "About you" notes, detected meeting topic, recent suggestions | The assistant provider chosen in Settings → Assistant: OpenAI, Anthropic, DeepSeek, a local server (Ollama, LM Studio) or a custom OpenAI-compatible address | When a question is detected (if auto-suggest is on) or when you press an Assist action. |
| A screenshot of your main display | The assistant provider (skipped for DeepSeek, which has no image input) | Only after you press the camera button; sent once with the next request, then discarded. On macOS, Meetco's own windows are excluded from it. |

All requests use **your own API key** and are billed to **your OpenAI account**. OpenAI's API data
usage policies apply to that data. You can point Meetco to a different OpenAI-compatible base URL
in Settings → OpenAI; your key and data are then sent to that address.

The assistant (answers, recaps, questions) can use a different provider in
Settings → Assistant. Requests then go to that provider with your key for it and are billed to
your account there; its data policies apply. With **Ollama** or **LM Studio** the assistant runs
on your own computer and transcript excerpts never leave it.

On macOS 26 and later, Settings → Audio → **Transcribe with: On this Mac** uses Apple's on-device
speech model (SpeechAnalyzer) instead of OpenAI: audio is never sent anywhere. macOS downloads the
model for each language once; it is shared with other apps. Combined with Ollama or LM Studio as the
assistant, Meetco makes no network requests during a meeting.

On Windows, Settings → Audio → **Transcribe with: On this PC** uses Whisper (whisper.cpp) instead
of OpenAI: audio is never sent anywhere. The Whisper model is downloaded once from the whisper.cpp
project on Hugging Face into `%LOCALAPPDATA%\Meetco\models` and checked against a fixed SHA-256;
that download is the only request it makes.

**Meetco does not process everything locally.** Speech recognition and AI suggestions happen on
OpenAI's servers. Voice activity detection, language identification and question detection run
on-device.

With **Detect questions in any language** (Settings → Assistant, on by default), the assistant
provider also receives a request for phrases the local detector did not recognise as questions, so
it decides whether they need an answer. These requests contain the same transcript excerpt as any
other answer request; there is simply more of them. Turn the setting off to send requests only for
recognised questions and for actions you trigger.

With **Meeting memory** (Settings → Assistant, on by default), Meetco sends the new transcript lines
to your assistant provider about every 30 seconds of conversation to update a short memory of the
meeting (people's names and roles as they were said, key facts, a summary). The memory stays in
memory only; it is written to disk only inside a saved transcript, when you enabled saving
transcripts. It is cleared when a new session starts.

## What stays on your computer

| | macOS | Windows |
|---|---|---|
| **API key** | macOS login Keychain (service `io.github.meetco.Meetco.openai`; other assistant providers use `io.github.meetco.Meetco.<provider>`) | Windows Credential Manager (generic credential `Meetco/OpenAI-API-Key`; other assistant providers use `Meetco/<Provider>-API-Key`) |
| **Preferences** | UserDefaults (`io.github.meetco.Meetco`) | `%APPDATA%\Meetco\settings.json` |
| **Saved transcripts** (only if enabled) | `~/Library/Application Support/Meetco/Sessions` | `%APPDATA%\Meetco\Sessions` |
| **Logs** (never contain the key or transcript text) | Console.app, subsystem `io.github.meetco.Meetco` | `%LOCALAPPDATA%\Meetco\logs` |

- The API key is never written to files, preferences or logs.
- **Audio** is kept in memory only and never written to disk.
- **Transcripts** are kept in memory for the current session. If you enable *Save transcripts* in
  Settings → Privacy, each session's transcript and suggestions are saved as JSON in the folder
  above. You can delete them from the same screen.

## Your controls

- Audio is captured only between **Start** and **Stop**, and the toolbar shows when Meetco is listening.
- Mute the microphone at any time from the toolbar.
- Clear the current transcript, or delete saved transcripts in Settings → Privacy.
- Delete the API key from Settings → OpenAI.

## Consent

Recording or transcribing conversations may require the consent of all participants where you
live. You are responsible for informing participants and complying with local laws and your
organization's policies.
