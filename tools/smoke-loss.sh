#!/usr/bin/env bash
# VR-072 DoD: stable at 2% packet loss. Re-runs the drive (prediction + cheat rejection), world sync (late join +
# restart hashes) and 8-player race tests with every bot dropping 2% of packets inside Unity Transport, on send and
# receive, on every channel (reliable ones must resend). Each underlying test keeps its own pass criteria.
# Usage: tools/smoke-loss.sh native|docker [lossPercent]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-native}"
LOSS="${2:-2}"
export BOT_ARGS="-simTransportLossPercent $LOSS"
fail() { echo "LOSS SMOKE FAIL: $*" >&2; exit 1; }

applied() { # <log dir>: every bot log must show the simulator active
  local n=0
  for log in "$1"/bot*.log; do
    grep -q "event=network_transport_simulation lossPercent=$LOSS " "$log" || fail "transport loss not applied in $log"
    n=$((n + 1))
  done
  [[ $n -gt 0 ]] || fail "no bot logs in $1"
}

"$ROOT/tools/smoke-drive.sh" "$MODE" | tail -3 || fail "drive test failed at ${LOSS}% loss"
applied "$ROOT/artifacts/smoke-drive"
"$ROOT/tools/smoke-world.sh" "$MODE" | tail -1 || fail "world test failed at ${LOSS}% loss"
applied "$ROOT/artifacts/smoke-world"
"$ROOT/tools/smoke-race.sh" "$MODE" | tail -1 || fail "race test failed at ${LOSS}% loss"
applied "$ROOT/artifacts/smoke-race"
echo "LOSS SMOKE PASS ($MODE, ${LOSS}% transport loss): drive, world sync and 8-player race all stable"
