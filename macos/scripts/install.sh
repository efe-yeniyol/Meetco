#!/usr/bin/env bash
# Builds Meetco (Release) and copies it to /Applications.
# Installing to a fixed location keeps macOS privacy permissions stable between launches.
set -euo pipefail
cd "$(dirname "$0")/.."
scripts/build.sh Release
osascript -e 'tell application id "io.github.meetco.Meetco" to quit' >/dev/null 2>&1 || true
rm -rf /Applications/Meetco.app
cp -R build/Meetco.app /Applications/Meetco.app
echo "✅ Installed /Applications/Meetco.app"
open /Applications/Meetco.app
