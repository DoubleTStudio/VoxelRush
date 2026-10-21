#!/usr/bin/env bash
# Alert conditions from docs/operations/MONITORING.md, evaluated on a server log (CI smokes, incident triage).
# Exits 1 and lists every triggered alert.
# Usage: tools/check-server-log.sh <server.log> [max-overrun-rate-percent]
set -euo pipefail
LOG="$1"; MAX_OVERRUN_PCT="${2:-1}"
alerts=0
alert() { echo "ALERT: $*"; alerts=$((alerts + 1)); }

grep -q "event=world_journal_failed" "$LOG" && alert "journal write failure (edits are being refused)"
grep -q "event=world_recovery_problem" "$LOG" && alert "world recovery problem: $(grep -m1 'event=world_recovery_problem' "$LOG" | cut -c1-160)"
grep -q "event=server_listen_failed" "$LOG" && alert "server could not listen"
grep -q "event=vehicle_input_abuse_disconnect" "$LOG" && alert "a client was disconnected for input abuse"
summary=$(grep "event=server_tick_summary" "$LOG" | tail -1 || true)
if [[ -n "$summary" ]]; then
  ticks=$(sed -nE 's/.*totalTicks=([0-9]+).*/\1/p' <<<"$summary"); over=$(sed -nE 's/.*totalOverruns=([0-9]+).*/\1/p' <<<"$summary")
  if [[ -n "$ticks" && "$ticks" -gt 0 ]] && awk -v o="$over" -v t="$ticks" -v m="$MAX_OVERRUN_PCT" 'BEGIN { exit !(o * 100 / t > m) }'; then
    alert "tick overruns $over of $ticks (> ${MAX_OVERRUN_PCT}%)"
  fi
fi
pending=$(grep "event=world_closed" "$LOG" | tail -1 | sed -nE 's/.*collapseAbandoned=([0-9]+).*/\1/p' || true)
[[ -n "$pending" && "$pending" -gt 0 ]] && alert "structural collapse searches abandoned: $pending"
rejects=$(grep -c "event=client_rejected" "$LOG" || true)
joins=$(grep -c "event=client_connected" "$LOG" || true)
[[ "$rejects" -gt 0 && "$rejects" -gt $((joins * 2 + 5)) ]] && alert "join rejections ($rejects) far exceed joins ($joins)"

if [[ $alerts -gt 0 ]]; then echo "$alerts alert(s) in $LOG"; exit 1; fi
echo "no alerts in $LOG"
