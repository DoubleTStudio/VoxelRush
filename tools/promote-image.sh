#!/usr/bin/env bash
# Staged release / rollback for server images: points <channel> (staging, beta, production) at sha-<commit>, after
# checking the channel's current image can hand its world data to the new one. Rolling back is promoting an older sha.
# Usage: tools/promote-image.sh <commit-sha> <staging|beta|production>     (needs docker login to ghcr.io)
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REGISTRY="ghcr.io/doubletstudio"
SHA="${1:0:12}"; CHANNEL="$2"
case "$CHANNEL" in staging|beta|production) ;; *) echo "unknown channel $CHANNEL" >&2; exit 2 ;; esac
target="$REGISTRY/voxelrush-server:sha-$SHA"
docker pull -q "$target" >/dev/null
if docker pull -q "$REGISTRY/voxelrush-server:$CHANNEL" >/dev/null 2>&1; then
  # Whether moving the channel forward or back, the new image must read what the current one wrote.
  "$ROOT/tools/rollback-check.sh" "$REGISTRY/voxelrush-server:$CHANNEL" "$target"
fi
for image in voxelrush-server voxelrush-api; do
  docker pull -q "$REGISTRY/$image:sha-$SHA" >/dev/null
  docker tag "$REGISTRY/$image:sha-$SHA" "$REGISTRY/$image:$CHANNEL"
  docker push -q "$REGISTRY/$image:$CHANNEL" >/dev/null
  echo "$REGISTRY/$image:$CHANNEL -> sha-$SHA"
done
