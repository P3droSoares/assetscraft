#!/usr/bin/env bash
# Baixa FFmpeg/ffprobe (LGPL, shared, builds do BtbN) para vendor/ffmpeg/<rid>/ (bin/ + lib/).
# O app embute esses binários: ffmpeg para o Claude (quadros de vídeos de referência), sem depender do sistema.
# Uso: scripts/fetch-ffmpeg.sh [linux-x64] [win-x64]      (sem argumento: os dois)
set -euo pipefail
cd "$(dirname "$0")/.."
VER="${FFMPEG_VERSION:-n9.0-latest}"
MAJOR="${VER%%-*}"; MAJOR="${MAJOR#n}"
BASE="https://github.com/BtbN/FFmpeg-Builds/releases/download/latest"
TMP="$(mktemp -d)"; trap 'rm -rf "$TMP"' EXIT

fetch() { # rid asset exe-suffix
  local rid=$1 asset=$2 exe=$3 dest="vendor/ffmpeg/$1"
  if [[ -f "$dest/bin/ffmpeg$exe" && -f "$dest/bin/ffprobe$exe" ]]; then echo "ok $rid (já baixado)"; return; fi
  echo ">> $rid: $asset"
  curl -fsSL --retry 3 -o "$TMP/$asset" "$BASE/$asset"
  mkdir -p "$TMP/$rid"
  case "$asset" in
    *.zip) python3 -m zipfile -e "$TMP/$asset" "$TMP/$rid" ;;
    *)     tar -xf "$TMP/$asset" -C "$TMP/$rid" ;;
  esac
  local root; root="$(find "$TMP/$rid" -mindepth 1 -maxdepth 1 -type d | head -1)"
  rm -rf "$dest"; mkdir -p "$dest/bin"
  cp "$root/bin/ffmpeg$exe" "$root/bin/ffprobe$exe" "$dest/bin/"     # ffplay não é necessário
  if [[ -n "$exe" ]]; then
    cp "$root"/bin/*.dll "$dest/bin/"                                 # Windows: DLLs ao lado dos .exe
  else
    mkdir -p "$dest/lib"                                              # Linux: RUNPATH=$ORIGIN/../lib
    # só os nomes SONAME (libavcodec.so.62), já desreferenciados: evita copiar 3x cada lib
    find "$root/lib" -maxdepth 1 -regextype posix-extended -regex '.*/lib[^/]+\.so\.[0-9]+$' \
      -exec cp -L {} "$dest/lib/" \;
  fi
  cp "$root/LICENSE.txt" "$dest/LICENSE.txt" 2>/dev/null || true
  chmod +x "$dest"/bin/ffmpeg* "$dest"/bin/ffprobe* || true
  du -sh "$dest"
}

for r in ${@:-linux-x64 win-x64}; do
  case "$r" in
    linux-x64) fetch linux-x64 "ffmpeg-${VER}-linux64-lgpl-shared-${MAJOR}.tar.xz" "" ;;
    win-x64)   fetch win-x64   "ffmpeg-${VER}-win64-lgpl-shared-${MAJOR}.zip" ".exe" ;;
    *) echo "rid não suportado: $r" >&2; exit 1 ;;
  esac
done
