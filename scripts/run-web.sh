#!/usr/bin/env bash
# Web アプリを起動し、待ち受けを始めたらブラウザで開く（WSL / Linux / macOS）。
# 使い方: ./scripts/run-web.sh   （終了は Ctrl+C）
set -euo pipefail

cd "$(dirname "$0")/.."
url="https://localhost:52017"

open_browser() {
    if grep -qi microsoft /proc/version 2>/dev/null; then
        cmd.exe /c start "" "$url" >/dev/null 2>&1   # WSL: Windows 側の既定ブラウザ
    elif command -v xdg-open >/dev/null; then
        xdg-open "$url" >/dev/null 2>&1
    elif command -v open >/dev/null; then
        open "$url"
    else
        echo "ブラウザで $url を開いてください"
    fi
}

dotnet run --project src/FaraPokemonAssistance.Web "$@" 2>&1 | while IFS= read -r line; do
    printf '%s\n' "$line"
    if [[ "$line" == *"Now listening on: $url"* ]]; then
        open_browser &
    fi
done
