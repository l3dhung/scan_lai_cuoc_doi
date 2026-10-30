#!/bin/bash
# Builds "Life-scanner.app" from dist/index.html.
# Usage: macos/build-app.sh [output-dir]   (default: ~/Applications)
# Re-run after regenerating dist/ to update the installed app.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
OUT_DIR="${1:-$HOME/Applications}"
APP="${OUT_DIR}/Life-scanner.app"
OLD_APP="${OUT_DIR}/Document Scanner.app"
VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$ROOT/app.config.json" | head -1)"
VERSION="${VERSION:-1.0.0}"

[ -f "$ROOT/dist/index.html" ] || { echo "Missing dist/index.html" >&2; exit 1; }

TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

mkdir -p "$TMP/app.app/Contents/MacOS" "$TMP/app.app/Contents/Resources/app"
cp "$ROOT/dist/index.html" "$TMP/app.app/Contents/Resources/app/index.html"
cp "$ROOT/macos/serve.py" "$TMP/app.app/Contents/Resources/serve.py"
cp "$ROOT/macos/launcher.sh" "$TMP/app.app/Contents/MacOS/DocumentScanner"
chmod +x "$TMP/app.app/Contents/MacOS/DocumentScanner"

# Icon: render the SVG favicon with Quick Look, then build an .icns.
ICONSET="$TMP/AppIcon.iconset"
mkdir -p "$ICONSET"
qlmanage -t -s 1024 -o "$TMP" "$ROOT/assets/favicon.svg" >/dev/null 2>&1 || true
ICON_ENTRY=""
if [ -f "$TMP/favicon.svg.png" ]; then
  for size in 16 32 128 256 512; do
    sips -z "$size" "$size" "$TMP/favicon.svg.png" --out "$ICONSET/icon_${size}x${size}.png" >/dev/null
    double=$((size * 2))
    sips -z "$double" "$double" "$TMP/favicon.svg.png" --out "$ICONSET/icon_${size}x${size}@2x.png" >/dev/null
  done
  iconutil -c icns "$ICONSET" -o "$TMP/app.app/Contents/Resources/AppIcon.icns"
  ICON_ENTRY="<key>CFBundleIconFile</key><string>AppIcon</string>"
fi

cat > "$TMP/app.app/Contents/Info.plist" <<PLIST
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
  <key>CFBundleName</key><string>Life-scanner</string>
  <key>CFBundleDisplayName</key><string>Life-scanner</string>
  <key>CFBundleIdentifier</key><string>local.htmlapps.document-scanner</string>
  <key>CFBundleExecutable</key><string>DocumentScanner</string>
  <key>CFBundlePackageType</key><string>APPL</string>
  <key>CFBundleShortVersionString</key><string>${VERSION}</string>
  <key>CFBundleVersion</key><string>${VERSION}</string>
  <key>LSMinimumSystemVersion</key><string>11.0</string>
  ${ICON_ENTRY}
</dict>
</plist>
PLIST

mkdir -p "$OUT_DIR"
# Stop a server left running by the previous build so the next launch uses the new code.
pkill -f "Life-scanner.app/Contents/Resources/serve.py" 2>/dev/null || true
pkill -f "Document Scanner.app/Contents/Resources/serve.py" 2>/dev/null || true
rm -rf "$APP"
mv "$TMP/app.app" "$APP"
codesign --force --sign - "$APP" >/dev/null 2>&1 || true
touch "$APP"
echo "Built: $APP (v${VERSION})"
# The app used to be called "Document Scanner": move that copy to the Trash (recoverable) so only one app remains.
if [ -d "$OLD_APP" ]; then
  TRASHED="$HOME/.Trash/Document Scanner $(date +%Y%m%d-%H%M%S).app"
  if mv "$OLD_APP" "$TRASHED"; then echo "Moved the old app to the Trash: $TRASHED"; else echo "Could not move $OLD_APP; delete it yourself if you no longer need it."; fi
fi
