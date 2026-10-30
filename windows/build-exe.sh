#!/bin/bash
# Builds windows/out/Life-scanner.exe (one self-contained file for 64-bit Windows 10/11) from dist/index.html.
# Works on macOS or Linux with the .NET 8 SDK; on Windows run:
#   dotnet publish windows\LifeScanner.csproj -c Release -o windows\out
# Rebuild dist/ first (node scripts/build-standalone.cjs) when the page changed.
set -euo pipefail

ROOT="$(cd "$(dirname "$0")/.." && pwd)"
DOTNET="$(command -v dotnet || true)"
[ -n "$DOTNET" ] || DOTNET="$HOME/.dotnet/dotnet"
[ -x "$DOTNET" ] || { echo "The .NET 8 SDK is required: https://dotnet.microsoft.com/download/dotnet/8.0" >&2; exit 1; }
[ -f "$ROOT/dist/index.html" ] || { echo "Missing dist/index.html" >&2; exit 1; }

VERSION="$(sed -n 's/.*"version"[[:space:]]*:[[:space:]]*"\([^"]*\)".*/\1/p' "$ROOT/app.config.json" | head -1)"
"$DOTNET" publish "$ROOT/windows/LifeScanner.csproj" -c Release -p:Version="${VERSION:-1.0.0}" -o "$ROOT/windows/out" --nologo
echo "Built: $ROOT/windows/out/Life-scanner.exe"
