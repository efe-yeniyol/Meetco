# Meetco Privacy & Data Handling

Meetco is a Bring-Your-Own-Key (BYOK) app. It has no Meetco account, no backend servers, no analytics and no telemetry.

## What leaves your Mac

| Data | Sent to | When |
|------|---------|------|
| Speech audio, streamed while someone is talking (24 kHz mono PCM) | OpenAI Realtime API (`wss://…/v1/realtime?intent=transcription`) | While listening, with the default `gpt-live-transcribe` model. Only detected speech is streamed; silence is not sent, and audio judged to be noise is discarded from the stream. |
| Short speech audio clips (16 kHz mono WAV, a few seconds each) | OpenAI `/v1/audio/transcriptions` | With a file-based transcription model, or as a fallback when the live connection is unavailable. Silence and very short noises are not sent. |
| Recent transcript excerpts, your "About you" notes, detected meeting topic, recent suggestions | OpenAI `/v1/chat/completions` | When a question is detected (if auto-suggest is on) or when you press an Assist action. |
| A screenshot of your main display (Meetco's own windows excluded) | OpenAI `/v1/chat/completions` | Only after you press the camera button; sent once with the next request, then discarded. |

All requests use **your own API key** and are billed to **your OpenAI account**. OpenAI's API data usage policies apply to that data. You can point Meetco to a different OpenAI-compatible base URL in Settings → OpenAI → Advanced; your key and data are then sent to that address.

**Meetco does not process everything locally.** Speech recognition and AI suggestions happen on OpenAI's servers. Language identification and question detection run on-device.

## What stays on your Mac

- **API key:** stored in the macOS login Keychain (service `io.github.meetco.Meetco.openai`). It is never written to files, UserDefaults or logs.
- **Audio:** kept in memory only, never written to disk.
- **Transcripts:** kept in memory for the current session. If you enable *Settings → Privacy → Save transcripts on this Mac*, each session's transcript and suggestions are saved as JSON in `~/Library/Application Support/Meetco/Sessions`. You can delete them from the same screen.
- **Preferences:** stored in UserDefaults (`io.github.meetco.Meetco`).

## Your controls

- Audio is captured only between **Start** and **Stop**; a pulsing red dot shows when Meetco is listening, and macOS shows its own recording indicator.
- Mute the microphone at any time from the toolbar.
- Clear the current transcript from the Transcript tab or Settings → Privacy.
- Delete the API key from Settings → OpenAI.

## Consent

Recording or transcribing conversations may require the consent of all participants where you live. You are responsible for informing participants and complying with local laws and your organization's policies.
