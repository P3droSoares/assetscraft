#!/usr/bin/env bash
# Gera executáveis self-contained (não precisam do .NET instalado) em dist/<rid>/
# Uso: scripts/publish.sh [linux-x64|win-x64|...]   (sem argumento: linux-x64 e win-x64)
set -euo pipefail
cd "$(dirname "$0")/.."
RIDS=("${@:-linux-x64 win-x64}")
for rid in ${RIDS[@]}; do
  echo ">> $rid"
  scripts/fetch-ffmpeg.sh "$rid"   # FFmpeg embutido (ferramenta do Claude para referências de vídeo)
  rm -rf "dist/$rid"
  dotnet publish src/AssetsCraft.Desktop -c Release -r "$rid" --self-contained -p:DebugType=none \
    -o "dist/$rid"
done
