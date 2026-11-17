#!/usr/bin/env bash
# Builds (Release) and launches Meetco.
set -euo pipefail
cd "$(dirname "$0")/.."
scripts/build.sh "${1:-Release}"
# Quit a running copy so the new build is launched.
osascript -e 'tell application id "io.github.meetco.Meetco" to quit' >/dev/null 2>&1 || true
sleep 0.5
open build/Meetco.app
echo "🚀 Meetco launched. Look for the floating toolbar at the top of your screen."
