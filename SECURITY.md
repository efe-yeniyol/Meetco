# Security Policy

## Supported versions

Only the latest release and the `main` branch receive security fixes.

## Reporting a vulnerability

**Please do not open a public issue for security problems.**

Use GitHub's private vulnerability reporting instead: open the repository's **Security** tab and choose **Report a vulnerability**. Include:

- what the problem is and what an attacker could do with it
- steps to reproduce, and the operating system (macOS or Windows) and Meetco versions you used
- any logs, with API keys, transcripts and other private data removed

You can expect an acknowledgement within a few days. Please give the maintainers reasonable time to release a fix before disclosing the issue publicly.

## How Meetco handles credentials

- The OpenAI API key is stored only in the operating system's secure store: the **macOS login Keychain** (service `io.github.meetco.Meetco.openai`) or the **Windows Credential Manager** (generic credential `Meetco/OpenAI-API-Key`). It is never written to files, preferences, logs or the repository, and Meetco never reads keys from environment variables or `.env` files.
- The key is sent only in the `Authorization` header to the configured API base URL (by default `https://api.openai.com/v1`). If you change the base URL, your key and meeting data go to that address.
- Logs never contain the key, request headers or transcript text.

## Keeping your own key safe when contributing

- Never paste a key into source files, tests, issues, pull requests or screenshots.
- Run `make check-secrets` before you push, or `make hooks` once to block commits that contain something that looks like a key.
- If a key was ever committed or posted, **revoke it immediately** at <https://platform.openai.com/api-keys> and create a new one. Deleting the commit is not enough: the key is already public.
