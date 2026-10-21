#!/usr/bin/env bash
# VR-013 load fixture: authoritative server + N headless driving bots (dev builds, 100 ms simulated RTT).
# Records the server's 30 Hz tick metrics while all N players are connected, writes artifacts/load/, asserts the budget.
# Usage: tools/load-test.sh native|docker [bots=8] [seconds=40]
set -euo pipefail

ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
MODE="${1:-native}"
BOTS="${2:-8}"
SECONDS_DRIVING="${3:-40}"
LOGS="$ROOT/artifacts/load"
PORT=7777
RTT_MS=100
SERVER_TICKS=$(( (SECONDS_DRIVING + 20) * 30 ))
PROCESS_TIMEOUT=$(( SECONDS_DRIVING + 90 ))
IMAGE="voxelrush-server:smoke"
NETWORK="voxelrush-load"
BOT_IMAGE="ubuntu:24.04@sha256:68ee22004a9109b44973313b02ff7b1de250f1de0f5ecb628d73f579aaff33ac"
pids=()

rm -rf "$LOGS" && mkdir -p "$LOGS"

cleanup() {
  for pid in "${pids[@]+"${pids[@]}"}"; do kill "$pid" 2>/dev/null || true; done
  if [[ "$MODE" == "docker" ]]; then
    docker rm -f voxelrush-load-server >/dev/null 2>&1 || true
    docker network rm "$NETWORK" >/dev/null 2>&1 || true
  fi
}
trap cleanup EXIT
fail() { echo "LOAD TEST FAIL: $*" >&2; exit 1; }

if [[ "$MODE" == "docker" ]]; then
  docker build -q -f "$ROOT/docker/server/Dockerfile" -t "$IMAGE" "$ROOT/build/server" >/dev/null
  docker network create "$NETWORK" >/dev/null
  docker run --rm --init --name voxelrush-load-server --network "$NETWORK" "$IMAGE" -port "$PORT" -maxPlayers 8 -smokeTestTicks "$SERVER_TICKS" >"$LOGS/server.log" 2>&1 &
  ENDPOINT="voxelrush-load-server:$PORT"
else
  perl -e 'alarm shift; exec @ARGV' "$PROCESS_TIMEOUT" "$ROOT/build/server-mac/VoxelRush" -batchmode -nographics -logFile - -port "$PORT" -maxPlayers 8 -smokeTestTicks "$SERVER_TICKS" >"$LOGS/server.log" 2>&1 &
  ENDPOINT="127.0.0.1:$PORT"
fi
SERVER_PID=$!
pids+=("$SERVER_PID")
for _ in $(seq 1 120); do grep -q "event=server_listening" "$LOGS/server.log" 2>/dev/null && break; sleep 0.5; done
grep -q "event=server_listening" "$LOGS/server.log" || fail "server never listened"

bot_pids=()
for i in $(seq 1 "$BOTS"); do
  args=(-batchmode -nographics -logFile - -connect "$ENDPOINT" -smokeTestSeconds "$SECONDS_DRIVING" -botDrive -simRttMs "$RTT_MS")
  if [[ "$MODE" == "docker" ]]; then
    docker run --rm --init --network "$NETWORK" --user 10001 -e HOME=/tmp -v "$ROOT/build/bot-linux:/bot:ro" "$BOT_IMAGE" \
      timeout --kill-after=10 "$PROCESS_TIMEOUT" /bot/VoxelRushBot.x86_64 "${args[@]}" >"$LOGS/bot$i.log" 2>&1 &
  else
    perl -e 'alarm shift; exec @ARGV' "$PROCESS_TIMEOUT" "$ROOT/build/bot-mac/VoxelRush" "${args[@]}" >"$LOGS/bot$i.log" 2>&1 &
  fi
  bot_pids+=("$!"); pids+=("$!")
  sleep 0.3
done

failed=0
for i in "${!bot_pids[@]}"; do code=0; wait "${bot_pids[$i]}" || code=$?; [[ $code -eq 0 ]] || { echo "bot$((i + 1)) exit $code"; failed=1; }; done
code=0; wait "$SERVER_PID" || code=$?
[[ $failed -eq 0 ]] || fail "a bot failed (see $LOGS/bot*.log)"
[[ $code -eq 0 ]] || fail "server exit $code"
[[ $(grep -c "event=vehicle_spawned " "$LOGS/server.log") -eq "$BOTS" ]] || fail "expected $BOTS vehicles spawned"
spawns=$(grep "event=vehicle_spawned " "$LOGS/server.log" | sed -nE 's/.*position="([^"]+)".*/\1/p' | sort -u | wc -l | tr -d ' ')
[[ "$spawns" -eq "$BOTS" ]] || fail "only $spawns distinct spawn points for $BOTS cars"

python3 -I - "$LOGS" "$BOTS" "$RTT_MS" <<'PY'
import json, re, sys
logs, bots, rtt = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
fields = lambda line: dict(re.findall(r'(\w+)=("[^"]*"|\S+)', line))
windows = [fields(l) for l in open(f"{logs}/server.log") if "event=server_tick_metrics " in l]
full = [w for w in windows if int(w["players"]) == bots]
with open(f"{logs}/tick_windows.csv", "w") as f:
    f.write("players,ticks,tickRate,p50Ms,p95Ms,p99Ms,maxMs,overruns\n")
    for w in windows:
        f.write(",".join(w[k] for k in ["players", "ticks", "tickRate", "p50Ms", "p95Ms", "p99Ms", "maxMs", "overruns"]) + "\n")
pred = []
for i in range(1, bots + 1):
    stats = [fields(l) for l in open(f"{logs}/bot{i}.log") if "event=vehicle_prediction_stats " in l]
    if stats:
        pred.append({k: float(stats[-1][k]) for k in ["rttMs", "meanErrorM", "maxErrorM", "snap", "snapshots"]})
ticks = sum(int(w["ticks"]) for w in full)
summary = {
    "bots": bots, "simulatedRttMs": rtt, "windowsAtFullLoad": len(full), "ticksAtFullLoad": ticks,
    "tickRateMin": min((float(w["tickRate"]) for w in full), default=0),
    "p50MsMax": max((float(w["p50Ms"]) for w in full), default=0),
    "p95MsMax": max((float(w["p95Ms"]) for w in full), default=0),
    "p99MsMax": max((float(w["p99Ms"]) for w in full), default=0),
    "tickMaxMs": max((float(w["maxMs"]) for w in full), default=0),
    "overruns": sum(int(w["overruns"]) for w in full),
    "overrunRate": sum(int(w["overruns"]) for w in full) / ticks if ticks else 1,
    "botRttMsMean": sum(p["rttMs"] for p in pred) / len(pred) if pred else -1,
    "botMeanErrorMMax": max((p["meanErrorM"] for p in pred), default=-1),
    "botSnapsMax": max((p["snap"] for p in pred), default=-1),
}
json.dump(summary, open(f"{logs}/summary.json", "w"), indent=2)
print(json.dumps(summary, indent=2))
problems = []
if summary["windowsAtFullLoad"] < 3: problems.append("fewer than 3 metric windows at full load")
if summary["tickRateMin"] < 29: problems.append(f"tick rate fell to {summary['tickRateMin']:.1f} Hz")
if summary["p95MsMax"] > 1000 / 30: problems.append(f"p95 tick {summary['p95MsMax']:.2f} ms over the 33.3 ms budget")
if summary["overrunRate"] > 0.01: problems.append(f"overrun rate {summary['overrunRate']:.2%} > 1%")
if len(pred) != bots: problems.append(f"prediction stats from {len(pred)}/{bots} bots")
if problems:
    print("LOAD TEST FAIL: " + "; ".join(problems), file=sys.stderr); sys.exit(1)
PY
echo "LOAD TEST PASS ($MODE, $BOTS bots, ${SECONDS_DRIVING}s, ${RTT_MS} ms RTT)"
