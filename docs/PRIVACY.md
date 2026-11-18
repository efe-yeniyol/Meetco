# Meetco Privacy & Data Handling

Meetco is a Bring-Your-Own-Key (BYOK) app for macOS and Windows. It has no Meetco account, no
backend servers, no analytics and no telemetry. Both apps handle data the same way.

## What leaves your computer

| Data | Sent to | When |
|------|---------|------|
| Speech audio, streamed while someone is talking (24 kHz mono PCM) | OpenAI Realtime API (`wss://…/v1/realtime?intent=transcription`) | While listening, with the default `gpt-live-transcribe` model. Only detected speech is streamed; silence is not sent, and audio judged to be noise is discarded from the stream. |
| Short speech audio clips (16 kHz mono WAV, a few seconds each) | OpenAI `/v1/audio/transcriptions` | With a file-based transcription model, or as a fallback when the live connection is unavailable. Silence and very short noises are not sent. |
| Recent transcript excerpts, your "About you" notes, detected meeting topic, recent suggestions | OpenAI `/v1/chat/completions` | When a question is detected (if auto-suggest is on) or when you press an Assist action. |
| A screenshot of your main display | OpenAI `/v1/chat/completions` | Only after you press the camera button; sent once with the next request, then discarded. On macOS, Meetco's own windows are excluded from it. |

All requests use **your own API key** and are billed to **your OpenAI account**. OpenAI's API data
usage policies apply to that data. You can point Meetco to a different OpenAI-compatible base URL
in Settings → OpenAI; your key and data are then sent to that address.

**Meetco does not process everything locally.** Speech recognition and AI suggestions happen on
OpenAI's servers. Voice activity detection, language identification and question detection run
on-device.

## What stays on your computer

| | macOS | Windows |
|---|---|---|
| **API key** | macOS login Keychain (service `io.github.meetco.Meetco.openai`) | Windows Credential Manager (generic credential `Meetco/OpenAI-API-Key`) |
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
