#!/usr/bin/env bash
# Builds Meetco.app into ./build/Meetco.app
# Usage: scripts/build.sh [Debug|Release]   (default: Release)
#
# Signing: macOS privacy permissions (Screen & System Audio Recording, Microphone) are tied
# to the app's code signature. Ad-hoc signatures change on every build, so macOS forgets the
# permission after each rebuild. To keep permissions stable, this script signs with an
# "Apple Development" certificate from your keychain when one is available.
#   MEETCO_SIGN_IDENTITY="Apple Development: Name (TEAMID)"  → use a specific certificate
#   MEETCO_SIGN_IDENTITY="-"                                 → force ad-hoc signing
# The chosen certificate (SHA-1) is remembered in .signing-identity (git-ignored) so every
# build uses the same one — changing certificates makes macOS forget granted permissions.
set -euo pipefail
cd "$(dirname "$0")/.."

CONFIGURATION="${1:-Release}"
DERIVED_DATA="build/DerivedData"
IDENTITY_FILE=".signing-identity"

IDENTITY="${MEETCO_SIGN_IDENTITY:-}"
if [[ -z "$IDENTITY" && -s "$IDENTITY_FILE" ]]; then
  IDENTITY="$(head -n 1 "$IDENTITY_FILE")"
  # Forget it if the certificate is no longer in the keychain.
  if [[ "$IDENTITY" != "-" ]] && ! security find-identity -v -p codesigning | grep -q "$IDENTITY"; then
    IDENTITY=""
  fi
fi
if [[ -z "$IDENTITY" ]]; then
  IDENTITY="$(security find-identity -v -p codesigning 2>/dev/null \
    | sed -n 's/.*) \([0-9A-F]\{40\}\) "Apple Development: .*/\1/p' | sort | head -n 1)"
  IDENTITY="${IDENTITY:--}"
  echo "$IDENTITY" > "$IDENTITY_FILE"
fi

if [[ "$IDENTITY" == "-" ]]; then
  echo "🔏 Signing ad-hoc (permissions must be re-granted after each rebuild)"
  SIGN_ARGS=(CODE_SIGN_IDENTITY=-)
else
  NAME="$(security find-identity -v -p codesigning | grep "$IDENTITY" | sed -n 's/.*"\(.*\)"/\1/p' | head -n 1)"
  echo "🔏 Signing with: ${NAME:-$IDENTITY}"
  SIGN_ARGS=(CODE_SIGN_IDENTITY="$IDENTITY" CODE_SIGN_STYLE=Manual OTHER_CODE_SIGN_FLAGS=--timestamp=none)
fi

# Optional: stamp a release version, e.g. MEETCO_VERSION=1.2.0 (set by the release workflow).
VERSION_ARGS=()
if [[ -n "${MEETCO_VERSION:-}" ]]; then
  VERSION_ARGS=(MARKETING_VERSION="${MEETCO_VERSION#v}")
fi

xcodebuild \
  -project Meetco.xcodeproj \
  -scheme Meetco \
  -configuration "$CONFIGURATION" \
  -derivedDataPath "$DERIVED_DATA" \
  -destination "platform=macOS" \
  "${SIGN_ARGS[@]}" \
  ${VERSION_ARGS[@]+"${VERSION_ARGS[@]}"} \
  build | (command -v xcpretty >/dev/null && xcpretty || grep -E "error:|warning:|BUILD" || true)

rm -rf build/Meetco.app
cp -R "$DERIVED_DATA/Build/Products/$CONFIGURATION/Meetco.app" build/Meetco.app
echo ""
echo "✅ Built build/Meetco.app ($CONFIGURATION)"
