# Roadmap

What we plan to build next. Nothing here is promised or dated. Priorities change with feedback, so
[open an issue](https://github.com/efe-yeniyol/Meetco/issues) if something matters to you.

Every item follows the same rules as the rest of Meetco: **local first, no Meetco server, no account,
opt-in for anything that is stored**, and the same behavior on macOS and Windows.

**Legend:** 🟢 next up · 🟡 planned · ⚪ exploring

---

## 1. Conversation & meeting memory

Meetco forgets everything when you stop listening. Transcripts can be saved as JSON
(Settings → Privacy), but nothing reads them back. Memory makes Meetco useful *across* meetings.

- 🟢 **Meeting history browser.** Search past sessions, read the transcript and answers, export to Markdown, delete.
- 🟢 **Post-meeting summary.** One click after a call: summary, decisions, action items and open questions, saved with the session.
- 🟡 **Recurring-meeting memory.** Recognise a follow-up call (same title or participants) and bring in what was said last time: "Last week you promised to send the pricing deck".
- 🟡 **Ask your meetings.** A chat over your history: "What did the customer say about integrations?" with links back to the transcript line.
- 🟡 **People memory.** Notes per contact or company (role, what they care about, past questions), used as context when they speak.
- ⚪ **Automatic follow-ups.** Draft a follow-up email or task list from the summary. Never sent automatically.

## 2. Knowledge base

Today the only context is your *About you* notes. A knowledge base lets Meetco answer from your real
material instead of guessing.

- 🟢 **Documents as context.** Add PDFs, Markdown, Word files or folders (product docs, pricing, FAQ, CV). Answers quote and cite them.
- 🟢 **On-device indexing.** Chunk and embed files locally; only the few relevant passages are sent with a question.
- 🟡 **Profiles / workspaces.** Separate knowledge and *About you* sets for "Sales", "Engineering", "Interviews as interviewer" and switch per meeting.
- 🟡 **Per-meeting brief.** Paste an agenda, a customer page or a ticket before the call; it is used for that session only.
- ⚪ **Connectors.** Read-only import from Notion, Google Drive, Confluence. Only if it can work without a Meetco server.

## 3. Answers

- 🟡 **Answer styles.** Short / detailed / bullet points, and a per-profile tone.
- 🟡 **Speaker names** instead of "Participants" / "You" (diarisation), so answers can address the right person.
- ⚪ **Live fact check.** Flag when something said contradicts your knowledge base.

## 4. Platforms & distribution

- 🟢 **Notarized macOS build** and **signed Windows build**, so the first launch needs no extra clicks.
- 🟢 **Auto-update** (Sparkle on macOS, an update check on Windows).
- 🟡 **Windows installer** (MSIX or winget) instead of a zip.
- 🟡 **Homebrew cask** (`brew install --cask meetco`).
- ⚪ **Linux** build.

## 5. Privacy & trust

- 🟡 **Encrypted local history** (key in Keychain / Credential Manager).
- 🟡 **Retention setting.** Delete sessions older than N days automatically.
- 🟡 **Redaction.** Mask emails, phone numbers and card numbers before anything is sent to a provider.
- 🟡 **Fully offline mode** in one switch: on-device transcription + local model, with a clear indicator.

---

## Done

- ✅ macOS and Windows apps with the same features
- ✅ Assistant providers: OpenAI, Claude, DeepSeek, local models (Ollama, LM Studio), any OpenAI-compatible server
- ✅ On-device transcription (Apple speech model on macOS 26+, Whisper on Windows)
- ✅ Meeting memory: people (names, roles, companies), key facts and a running summary of the current meeting, used in every answer
- ✅ 57 languages: automatic language detection, question detection in any language, answers and translations in any language
- ✅ One-click downloads: macOS DMG and Windows zips on GitHub Releases

Want to help with one of these? See [CONTRIBUTING.md](CONTRIBUTING.md).
