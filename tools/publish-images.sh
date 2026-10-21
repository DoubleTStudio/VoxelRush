#!/usr/bin/env bash
# VR-081 (CI on main): builds the server and API images with version labels, runs the rollback drill against the
# previously published :main server image, then pushes :sha-<commit> and :main to GHCR.
# Needs: build/server, build/bot-linux, build/api; GHCR_TOKEN (the workflow's GITHUB_TOKEN with packages:write).
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
REGISTRY="ghcr.io/doubletstudio"
SHA="$(git -C "$ROOT" rev-parse --short=12 HEAD)"
LABELS=(); while IFS= read -r line; do LABELS+=("$line"); done < <("$ROOT/tools/image-labels.sh")

echo "$GHCR_TOKEN" | docker login ghcr.io -u "${GITHUB_ACTOR:-ci}" --password-stdin >/dev/null
docker build -q "${LABELS[@]}" -f "$ROOT/docker/server/Dockerfile" -t "$REGISTRY/voxelrush-server:sha-$SHA" "$ROOT/build/server" >/dev/null
docker build -q --label "org.opencontainers.image.revision=$(git -C "$ROOT" rev-parse HEAD)" --label "org.opencontainers.image.source=https://github.com/DoubleTStudio/VoxelRush" \
  -f "$ROOT/docker/api/Dockerfile" -t "$REGISTRY/voxelrush-api:sha-$SHA" "$ROOT/build/api" >/dev/null

if docker pull -q "$REGISTRY/voxelrush-server:main" >/dev/null 2>&1; then
  code=0; "$ROOT/tools/rollback-drill.sh" "$REGISTRY/voxelrush-server:sha-$SHA" "$REGISTRY/voxelrush-server:main" || code=$?
  if [[ $code -eq 2 ]]; then echo "::warning::Rollback to the previous :main is blocked by data versions; mark this release no-rollback."
  elif [[ $code -ne 0 ]]; then echo "::error::Rollback drill failed"; exit 1; fi
else
  echo "::notice::No previous :main server image; first publish, rollback drill skipped."
fi

for image in voxelrush-server voxelrush-api; do
  docker tag "$REGISTRY/$image:sha-$SHA" "$REGISTRY/$image:main"
  docker push -q "$REGISTRY/$image:sha-$SHA" >/dev/null
  docker push -q "$REGISTRY/$image:main" >/dev/null
  echo "published $REGISTRY/$image:sha-$SHA (+ :main)"
done
