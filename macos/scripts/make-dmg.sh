#!/usr/bin/env bash
# Packages build/Meetco.app into build/Meetco-macOS.dmg (drag-to-Applications installer).
# Usage: scripts/make-dmg.sh   (run scripts/build.sh first, or use `make dmg`)
# Uses only tools that ship with macOS (hdiutil), so it also runs on CI.
set -euo pipefail
cd "$(dirname "$0")/.."

APP="build/Meetco.app"
DMG="build/Meetco-macOS.dmg"
VOLUME="Meetco"

[[ -d "$APP" ]] || { echo "❌ $APP not found. Run scripts/build.sh first." >&2; exit 1; }

STAGING="$(mktemp -d)"
trap 'rm -rf "$STAGING"' EXIT

ditto "$APP" "$STAGING/Meetco.app"
ln -s /Applications "$STAGING/Applications"

rm -f "$DMG"
hdiutil create \
  -volname "$VOLUME" \
  -srcfolder "$STAGING" \
  -fs HFS+ \
  -format UDZO \
  -imagekey zlib-level=9 \
  -ov "$DMG" >/dev/null

hdiutil verify "$DMG" >/dev/null
echo "✅ Built $DMG ($(du -h "$DMG" | cut -f1 | xargs))"
