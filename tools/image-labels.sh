#!/usr/bin/env bash
# Prints docker --label arguments with the wire/data versions a server image speaks, read from the source constants.
# Rollbacks compare these (tools/rollback-check.sh); see docs/operations/RELEASE.md.
set -euo pipefail
ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
R="$ROOT/unity/Assets/VoxelRush/Runtime"
val() { sed -nE "s/.*$2 = ([0-9]+);.*/\1/p" "$1" | head -1; }
protocol=$(val "$R/Core/SimulationConstants.cs" "const ushort ProtocolVersion")
schema=$(val "$R/World/Chunks/ChunkData.cs" "const int CurrentSchemaVersion")
journal=$(val "$R/World/Persistence/JournalCodec.cs" "const byte RecordVersion")
terrain=$(val "$R/World/Generation/TerrainGenerator.cs" "const int Version")
plot=$(val "$R/World/Generation/FlatPlotGenerator.cs" "const int Version")
for v in protocol schema journal terrain plot; do [[ -n "${!v}" ]] || { echo "could not read $v version" >&2; exit 1; }; done
revision=$(git -C "$ROOT" rev-parse HEAD 2>/dev/null || echo unknown)
printf '%s\n' \
  --label "org.opencontainers.image.source=https://github.com/DoubleTStudio/VoxelRush" \
  --label "org.opencontainers.image.revision=$revision" \
  --label "voxelrush.protocol=$protocol" \
  --label "voxelrush.chunk-schema=$schema" \
  --label "voxelrush.journal=$journal" \
  --label "voxelrush.generator.terrain=$terrain" \
  --label "voxelrush.generator.flatplot=$plot"
