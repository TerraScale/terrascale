# TerraBase observability operator runbook

This runbook covers the private Prometheus boundary managed by the
Orchestrator. Use the dashboard for bounded metrics and the member logs for
correlation; never expose the metrics listener publicly.

## First response

1. Confirm the private scrape target and content type. Check the scrape-loss
   alert before treating an empty dashboard as healthy.
2. Check `terrabase_liveness`, `terrabase_started`, and `terrabase_ready`
   separately. A live but unready process has withdrawn from traffic; do not
   kill it solely for that condition.
3. Inspect `terrabase_component_health`. Storage, replication, and membership
   gate admission. Control-plane loss blocks new control mutations but does
   not withdraw an admitted member from the data path.
4. Compare request rate and outcomes by bounded Region and operation labels.
   Separate invalid, unauthenticated, and over-limit refusals from service
   errors.
5. Compare successful p50, p95, and p99 latency, then inspect storage
   capacity, replication lag, frontier health, and active Members.

## Saturation and durability

- Compare queue depth with capacity for the same member and queue. Identify
  foreground, commit, replication, backup, or usage-publication pressure.
- Use in-flight operations and admission refusals to distinguish active work
  from bounded capacity or lifecycle protection. Do not turn a refusal into a
  service error.
- Any WAL fsync or commit error is a durability incident. Retain the WAL and
  filesystem while following the recovery procedure; never acknowledge a
  failed or indeterminate commit as successful.
- If storage reaches the configured critical threshold, use the Orchestrator's
  placement or capacity action. Never delete WAL, history, snapshots, or
  backups by hand.

## Replication, backup, and usage

- Treat frontier health at or below zero as loss of a trustworthy frontier.
  Protect healthy Regions and follow repair; never advance a frontier based
  only on elapsed time or control-plane opinion.
- `GlobalDurable` requires two active selected Regions. If that condition is
  lost, refuse new acknowledgements of that class and retain the causal
  frontier.
- A continuous backup age beyond the configured recovery window requires
  checking the backup mode, destination, and Orchestrator state before retry.
- Usage-publication failures resume from the durable frontier. Do not mint
  replacement facts or reset the sequence to hide an alert.

## Safe actions and escalation

- For scrape failure, repair private routing and bounded timeouts. Never make
  the endpoint public.
- For readiness loss caused by storage or replication, retain the process
  and perform the owning recovery action before restarting.
- For a planned drain, set readiness false through the Orchestrator, wait for
  handoff, and verify readiness after replacement.
- Page only on customer-visible SLO burn. Escalate broad outages and security
  events through the staffed incident process; rotate credentials through the
  owning authority if a secret may have entered diagnostics.
