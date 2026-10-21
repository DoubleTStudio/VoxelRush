# Monitoring and alerts (VR-081)

Servers log one structured line per event (`event=name key=value …`, `LogEvent`). Ship stdout to the log store of the host (e.g. Loki/CloudWatch) and alert on the conditions below. `tools/check-server-log.sh <log>` evaluates the same conditions on a file; CI runs it on every smoke server.

## Alerts (DEPLOYMENT: tick overruns, write backlog, journal errors, join failures, OOM, mismatch rate)
| Alert | Condition | Severity | First response |
|---|---|---|---|
| Journal failure | any `event=world_journal_failed` | page | Edits are refused (by design). Check disk/volume; restart drains what it can. |
| Recovery problem | any `event=world_recovery_problem` | page | Torn tail or bad snapshot on start; compare `records`/`truncatedBytes` in `world_recovered`. |
| Tick overruns | `server_tick_metrics overruns` > 1% of ticks over 5 min, or `server_tick_summary` > 1% | ticket | Correlate with `players`, `snapshotsSent`; profile (VR-013/070 baselines in `docs/perf`). |
| Join failures | `client_rejected` far above `client_connected` (> 2× + 5) | ticket | `reason=`: `ticket_*` (API/clock skew), `server_full`, `protocol_mismatch` (client/server versions). |
| Input abuse | any `vehicle_input_abuse_disconnect` | info | A client sent out-of-contract inputs; check `rejectedMalformed`. |
| Collapse abandoned | `world_closed collapseAbandoned` > 0 | ticket | A structural search exceeded its budget; inspect the structure. |
| Replica mismatch | client `world_replica_stats hashMismatches` > 0 | ticket | Snapshot/delta corruption or generator mismatch. |
| Container OOM / restart | host metric | page | Memory per chunk is bounded by tier; check world size. |

## Dashboards
Tick p50/p95/p99 and overruns (`server_tick_metrics`), players, `net_bandwidth` per client, journal `durable` count growth, `world_collapse` activity, race phases (`race_phase`), joins/rejections by reason, API `/healthz` and HTTP 4xx/5xx/429 rates.
