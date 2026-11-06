#!/usr/bin/env bash
# Packs dist/<rid> (from publish.sh) into release archives with stable names, so
# https://github.com/<owner>/assetscraft/releases/latest/download/AssetsCraft-<rid>.<ext> always works.
#   dist/AssetsCraft-win-x64.zip        (AssetsCraft/AssetsCraft.exe)
#   dist/AssetsCraft-linux-x64.tar.gz   (AssetsCraft/AssetsCraft, keeps exec bits and symlinks)
set -euo pipefail
cd "$(dirname "$0")/../dist"
for rid in "${@:-win-x64 linux-x64}"; do
  for r in $rid; do
    [[ -d "$r" ]] || { echo "dist/$r não existe: rode scripts/publish.sh $r antes" >&2; exit 1; }
    rm -rf pkg && mkdir pkg && cp -a "$r" pkg/AssetsCraft
    case "$r" in
      win-*) rm -f "AssetsCraft-$r.zip"; (cd pkg && python3 -m zipfile -c "../AssetsCraft-$r.zip" AssetsCraft) ;;
      *)     rm -f "AssetsCraft-$r.tar.gz"; tar -C pkg -czf "AssetsCraft-$r.tar.gz" AssetsCraft ;;
    esac
    rm -rf pkg
    echo "ok  dist/AssetsCraft-$r.$( [[ $r == win-* ]] && echo zip || echo tar.gz )"
  done
done
