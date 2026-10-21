#!/usr/bin/env bash
# Can we roll a world back from <current image> to <target image>? Allowed only if the target reads everything the
# current one may have written: same journal record version, chunk schema and generator versions (DEPLOYMENT:
# "roll back server only after verifying generator/schema compatibility"). A protocol change is allowed but
# reported: clients must match the target protocol.
# Usage: tools/rollback-check.sh <current-image> <target-image>
set -euo pipefail
current="$1"; target="$2"
label() { docker image inspect "$1" --format "{{ index .Config.Labels \"$2\" }}" 2>/dev/null; }
status=0
for key in voxelrush.journal voxelrush.chunk-schema voxelrush.generator.terrain voxelrush.generator.flatplot; do
  a=$(label "$current" "$key"); b=$(label "$target" "$key")
  if [[ -z "$a" || -z "$b" || "$a" == "<no value>" || "$b" == "<no value>" ]]; then echo "ROLLBACK BLOCKED: $key label missing"; status=1
  elif [[ "$a" != "$b" ]]; then echo "ROLLBACK BLOCKED: $key $a -> $b (data written by $current may be unreadable)"; status=1
  else echo "ok: $key $a"; fi
done
pa=$(label "$current" voxelrush.protocol); pb=$(label "$target" voxelrush.protocol)
[[ "$pa" == "$pb" ]] && echo "ok: protocol $pa" || echo "NOTE: protocol $pa -> $pb (clients must speak $pb)"
exit $status
