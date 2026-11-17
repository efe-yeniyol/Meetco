#!/usr/bin/env bash
# Fails if anything that looks like a credential is about to be published.
#
# Usage:
#   scripts/check-secrets.sh            # scan every file git would publish (tracked + untracked, not ignored)
#   scripts/check-secrets.sh --staged   # scan only staged changes (used by the pre-commit hook)
#
# Install as a pre-commit hook:  make hooks
set -euo pipefail
cd "$(dirname "$0")/.."

# Credential patterns: OpenAI, Anthropic, GitHub, Slack, AWS, Google, private keys, bearer tokens.
PATTERN='(sk-(proj-|svcacct-|admin-)?[A-Za-z0-9_-]{20,}|sk-ant-[A-Za-z0-9_-]{20,}|gh[pousr]_[A-Za-z0-9]{30,}|github_pat_[A-Za-z0-9_]{30,}|xox[abprs]-[A-Za-z0-9-]{10,}|AKIA[0-9A-Z]{16}|AIza[0-9A-Za-z_-]{35}|-----BEGIN [A-Z ]*PRIVATE KEY-----|Bearer [A-Za-z0-9._-]{30,})'

# Files that must never be published, whatever their content.
FORBIDDEN='(^|/)(\.env(\..*)?|.*\.(p12|pem|key|cer|mobileprovision|provisionprofile|keychain(-db)?)|\.signing-identity|secrets.*)$'

if [[ "${1:-}" == "--staged" ]]; then
  FILES="$(git diff --cached --name-only --diff-filter=ACMR)"
else
  FILES="$(git ls-files --cached --others --exclude-standard)"
fi

status=0
if [[ -n "$FILES" ]]; then
  if bad_files="$(grep -E "$FORBIDDEN" <<<"$FILES")"; then
    echo "❌ These files must not be published:"
    sed 's/^/   /' <<<"$bad_files"
    status=1
  fi
  # Print only file:line, never the matching text, so a secret does not end up in terminal logs.
  # This script contains the patterns themselves, so it is skipped.
  if hits="$(tr '\n' '\0' <<<"$FILES" | xargs -0 grep -InE "$PATTERN" -- 2>/dev/null | grep -v '^scripts/check-secrets.sh:' | cut -d: -f1,2)"; then
    if [[ -n "$hits" ]]; then
      echo "❌ Possible credentials found (file:line):"
      sed 's/^/   /' <<<"$hits"
      status=1
    fi
  fi
fi

if [[ $status -eq 0 ]]; then
  echo "✅ No credentials found."
else
  echo ""
  echo "Remove the secret (and rotate it if it was ever pushed). API keys belong in the macOS Keychain only."
fi
exit $status
