#!/usr/bin/env bash
# VR-032 DoD: SIGKILL the journal writer at random moments; every ACKed edit must survive recovery.
# Usage: tools/crash-test.sh [iterations=20]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
ITERATIONS="${1:-20}"
OUT="$ROOT/artifacts/crash-test"
WORLD="$OUT/world"
rm -rf "$OUT" && mkdir -p "$WORLD"

dotnet build -c Release "$ROOT/tests/VoxelRush.CrashHarness" -o "$OUT/bin" >/dev/null
HARNESS="$OUT/bin/VoxelRush.CrashHarness.dll"

total_acks=0
for i in $(seq 1 "$ITERATIONS"); do
  dotnet "$HARNESS" write "$WORLD" >"$OUT/acks-$i.txt" 2>"$OUT/write-$i.err" &
  pid=$!
  # Random lifetime 0.4–2.0 s, then a hard kill mid-whatever (append, fsync, snapshot rename...).
  sleep "$(awk -v r=$RANDOM 'BEGIN { printf "%.2f", 0.4 + (r % 160) / 100 }')"
  kill -9 "$pid" 2>/dev/null || true
  wait "$pid" 2>/dev/null || true
  cat "$OUT"/acks-*.txt >"$OUT/all-acks.txt"
  if ! dotnet "$HARNESS" verify "$WORLD" "$OUT/all-acks.txt" >"$OUT/verify-$i.txt"; then
    cat "$OUT/verify-$i.txt"
    echo "CRASH TEST FAIL at iteration $i" >&2
    exit 1
  fi
  acks=$(grep -c '^ACK ' "$OUT/acks-$i.txt" || true)
  total_acks=$((total_acks + acks))
  echo "iteration $i: killed after $acks new ACKs; $(tail -1 "$OUT/verify-$i.txt")"
done
echo "CRASH TEST PASS: $ITERATIONS SIGKILLs, $total_acks acknowledged edits, none lost"
