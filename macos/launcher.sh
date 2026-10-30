#!/bin/bash
# Entry point inside "Life-scanner.app" (Contents/MacOS/DocumentScanner).
# Starts the loopback server if needed, then opens the app in the default browser.
set -u

RES="$(cd "$(dirname "$0")/../Resources" && pwd)"
PORT=47821
URL="http://127.0.0.1:${PORT}/"
PING="${URL}.document-scanner-ping"

is_ours() {
  [ "$(curl -s --max-time 1 "$PING" 2>/dev/null)" = "document-scanner" ]
}

if ! /usr/bin/python3 -I -c '' >/dev/null 2>&1; then
  # No usable Python: open the file directly (camera may be unavailable in Safari).
  open "file://${RES}/app/index.html"
  exit 0
fi

if ! is_ours; then
  nohup /usr/bin/python3 -I "${RES}/serve.py" "${RES}/app" "$PORT" >/dev/null 2>&1 &
  for _ in $(seq 1 50); do
    is_ours && break
    sleep 0.1
  done
fi

if is_ours; then
  open "$URL"
else
  # Port taken by something else: fall back to the local file.
  open "file://${RES}/app/index.html"
fi
