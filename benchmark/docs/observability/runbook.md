# TerraBase observability operator runbook

This runbook applies to the private production metrics boundary managed by
`ts-orchestrator`. Repository tests prove the metric contract. Only a
candidate-bound deployment receipt proves that the listener, collectors,
dashboard, and scrape path are deployed correctly.

## First response

1. Confirm that Prometheus is scraping the private authenticated TerraBase
   target and that the response content type is correct. Check the
   `TerraBaseScrapeMissing` alert before trusting an empty dashboard.
2. Check `terrabase_liveness`, `terrabase_started`, and `terrabase_ready`
   separately. A live process with `terrabase_ready 0` has withdrawn from
   traffic or failed admission or a required dependency. Do not kill it solely
   because it is unready.
3. Inspect `terrabase_component_health`. `1` is healthy, `0.5` is degraded,
   `0` is unhealthy, and `-1` means unknown or unconfigured. Storage,
   replication, and membership gate post-admission readiness. Control-plane
   loss blocks new control mutations but does not withdraw an admitted Member
   from data-plane service.
4. Check total requests per second, then split by `region`, `operation`, and
   `outcome`. A falling rate may be upstream routing or admission, while a
   rising `error` or `timeout` rate points at TerraBase or a dependency.
   `rejected` is separate because invalid, unauthenticated, and over-limit work
   does not enter the availability numerator in the same way.
5. Compare p50, p95, and p99 request latency for successful requests. A broad
   shift suggests saturation. A single operation or Region points to a
   narrower engine, storage, or network path.
6. Check storage bytes, capacity, and the 24-hour change. Missing capacity is a
   telemetry gap. It does not mean zero usage or unlimited space.
7. Check replication lag and active Members for the affected Regions. Confirm
   the selected database topology. The collector emits active Member alerts
   only for selected, known Regions; every selected production Region requires
   two active Members.

## Saturation and admission response

- Compare `terrabase_queue_depth` with `terrabase_queue_capacity` for the same
  `instance` and `queue`. Never sum capacities across Members. If the ratio is
  over 80 percent, identify whether foreground, commit, replication,
  compaction, backup, or usage export owns the pressure.
- Use `terrabase_in_flight_operations` and
  `terrabase_admission_rejections_total` to distinguish active work from
  capacity, concurrency, queue-full, deadline, or lifecycle refusals. Do not
  reclassify a refusal as an engine error.
- Queue-full rejection is evidence that the bounded protection worked. Remove
  the cause or execute the qualified capacity plan before changing its bound.

## Durability response

- Any increase in `terrabase_wal_fsync_total{outcome="error"}` is a durability
  incident. Preserve the WAL, filesystem state, candidate identity, and failed
  operation evidence. Do not convert it into a successful acknowledgement.
- `terrabase_commits_total{outcome="unknown"}` requires resolution under the
  original operation identity. A blind retry under a new identity can create a
  second logical write.
- Correlate commit latency with WAL fsync latency, queue saturation, and
  storage errors. Request latency alone cannot locate a durability stall.

## Storage response

- At 80 percent utilization, verify the collector's usable-capacity value,
  current growth, compaction debt, and planned placement. Prepare expansion or
  movement before the 90 percent critical threshold.
- At 90 percent utilization, stop nonessential storage growth through the
  approved admission controls and execute the qualified expansion or movement
  plan. Do not delete WAL, segments, backups, or history by hand.
- If bytes rise while request rate is flat, inspect compaction, retained
  history, indexes, backup staging, and repair. Storage usage alone cannot say
  which subsystem owns the growth.
- If `terrabase_storage_capacity_bytes` disappears, restore the collector and
  use provider capacity evidence until it returns. Do not silence the gap by
  publishing a guessed value.

## Replication, backup, and usage response

- Treat `terrabase_replication_frontier_health <= 0` for a selected Region as
  loss of a trustworthy frontier. Check backlog and lag, protect the healthy
  source, and follow the repair procedure. Never advance a retained frontier
  from elapsed time or a control-plane opinion.
- A continuous backup age over 24 hours violates the daily verified recovery
  point operating threshold. Inspect the bounded mode and backup result,
  destination health, and verification receipt before retrying.
- Usage-export frontier regression is refused in process. On export failures,
  preserve the durable frontier and resume from it. Do not mint replacement
  facts or reset the sequence to make the alert disappear.

## Safe actions

- If the scrape fails, verify private routing, workload identity, and the
  adapter's bounded timeout. Never make the endpoint public as a recovery
  action.
- If readiness is false because storage is unhealthy, preserve the process
  and its WAL evidence. Follow the storage recovery procedure and record the
  exact candidate, topology, and time before a restart.
- If replication is degraded, protect the remaining healthy Regions and avoid
  forcing a false durable acknowledgement. Follow the replication repair
  procedure. `terrabase_replication_lag_seconds` is an observation, not proof
  of a zero-loss durability guarantee.
- If error rates rise, inspect outcome and operation families before changing
  capacity. Invalid, authentication, and over-limit rejections must not become
  TerraBase availability errors.
- During a planned drain, set readiness false through the integration-owned
  admission lifecycle, wait for the orchestrated handoff, and verify readiness
  after the replacement is qualified.

## Alert policy

`deploy/observability/prometheus-rules.yml` defines the repository alert policy
for lifecycle, required and optional component health, error ratio, queues,
storage, WAL and commit outcomes, replication, compaction, backup, and usage
export. The optional-component rule fires on `0`, not `-1`, so an unconfigured
backup or metrics collector does not page. Rule changes require a versioned
configuration identity and a new qualification receipt.

## Evidence and escalation

Retain the redacted Prometheus snapshot, alert timestamps, source revision,
artifact and configuration identity, dashboard and rule versions, Region
topology, exact action, result, and known telemetry gaps. A dashboard
screenshot alone is not a qualification receipt. Preserve failed and invalid
observations. Escalate a broad outage or suspected security event under the
staffed Sev1 process. Rotate credentials through the approved authority if a
secret may have entered diagnostics.
