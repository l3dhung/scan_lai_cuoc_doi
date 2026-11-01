#!/bin/bash
# Renders assets/app-icon.png (1024 px, transparent corners), windows/app.ico and the Store/Start-menu logos in
# windows/msix/Assets from assets/favicon.svg. macOS only: Quick Look, sips and ImageMagick (`magick`).
# Run it again whenever the icon changes, then commit the PNGs.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/../.." && pwd)"
OUT="$ROOT/windows/msix/Assets"
BG="0F6E56"
TMP="$(mktemp -d)"
trap 'rm -rf "$TMP"' EXIT

command -v magick >/dev/null || { echo "ImageMagick is required (brew install imagemagick)" >&2; exit 1; }
qlmanage -t -s 1024 -o "$TMP" "$ROOT/assets/favicon.svg" >/dev/null 2>&1
[ -f "$TMP/favicon.svg.png" ] || { echo "Could not render assets/favicon.svg" >&2; exit 1; }
# Quick Look paints the page white behind the rounded square, so cut the corners out again (rx 14 of 64).
SRC="$ROOT/assets/app-icon.png"
magick "$TMP/favicon.svg.png" \( -size 1024x1024 xc:none -fill white -draw "roundrectangle 0,0 1023,1023 224,224" \) \
  -compose DstIn -composite -strip "$SRC"
magick "$SRC" -define icon:auto-resize=256,128,64,48,32,24,16 "$ROOT/windows/app.ico"
rm -rf "$OUT"
mkdir -p "$OUT"

square() { magick "$SRC" -filter Lanczos -resize "$2x$2" -strip "$OUT/$1"; }

for pair in 100:44 125:55 150:66 200:88 400:176; do square "Square44x44Logo.scale-${pair%%:*}.png" "${pair#*:}"; done
for size in 16 24 32 48 256; do
  square "Square44x44Logo.targetsize-$size.png" "$size"
  cp "$OUT/Square44x44Logo.targetsize-$size.png" "$OUT/Square44x44Logo.targetsize-${size}_altform-unplated.png"
done
for pair in 100:150 125:188 150:225 200:300 400:600; do square "Square150x150Logo.scale-${pair%%:*}.png" "${pair#*:}"; done
for pair in 100:50 125:63 150:75 200:100 400:200; do square "StoreLogo.scale-${pair%%:*}.png" "${pair#*:}"; done
# Wide tile: the icon centred on its own background colour.
for pair in 100:150 200:300; do
  h="${pair#*:}"
  magick "$SRC" -filter Lanczos -resize "${h}x$h" -background "#$BG" -gravity center -extent "$((h * 310 / 150))x$h" -strip \
    "$OUT/Wide310x150Logo.scale-${pair%%:*}.png"
done
echo "Wrote $SRC and $(ls "$OUT" | wc -l | tr -d ' ') images to $OUT"
