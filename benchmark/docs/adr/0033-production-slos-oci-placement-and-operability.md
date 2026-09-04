---
date: 2026-08-29
authority: ../../../GOAL.md
---

# Production SLOs, OCI placement, and operability

## Region identity and placement

TerraScale uses uppercase three-letter airport codes as `RegionCode` values.
The launch catalog is `GRU`, `SFO`, and `FRA`; the OCI provider mapping is
owned by the Orchestrator and never appears in public contracts, database
identities, checkpoints, causal state, or customer-visible logs. `SFO` stays
the TerraScale code even where OCI uses `SJC`.

The launch placement is two TerraBase member hosts per selected Region. The
members use OCI Ampere ARM64 by default, with native x86_64 as a supported
compatibility target. Members have independent storage attachments and are
placed in separate fault domains (and, where available, separate availability
domains). The Orchestrator refuses a Region that cannot provide the RF=2
member pair.

Members run on private regional networks. Cross-Region replication uses the
member transport; private service routes provide access to Vault and Object
Storage. Public ingress terminates only documented public services. A member,
TBEP listener, Vault route, storage attachment, or backup endpoint never has a
public ingress path.

## Availability and latency

The public API publishes a 99.9% monthly availability target per Region.
Invalid credentials, customer over-limit requests, customer network failures,
and an explicitly suspended database are outside the numerator; TerraScale
errors, dependency failures, capacity exhaustion, failover, and maintenance
are unavailable time.

For 1 KiB hot documents at qualified admitted load, the targets are:

| Operation | Target |
| --- | --- |
| Same-Region point read | p99 ≤ 10 ms |
| `RegionalDurable` write | p99 ≤ 20 ms |
| Local reactive visibility over WebSocket | p99 ≤ 50 ms |
| CDN reactive visibility | p99 ≤ 250 ms |
| Cross-Region visibility | p50 ≤ 300 ms; p99 ≤ 1 s |

The targets are measured at the public API boundary with the member resource
ceiling from the workspace goal. Queueing, authorization, replication, view
maintenance, and delivery are part of the measured path.

## Durability and recovery

`RegionalDurable` acknowledges after the local RF=2 WAL follower has synced
the frame. `GlobalDurable` acknowledges only after every selected Region has
durably accepted the row and its causal dependencies; it fails closed before
acknowledgement when fewer than two selected Regions are active. A one-Region
database uses `RegionalDurable` or `BackupDurable` instead.

Litestream backs up each SQLite file to Object Storage in the same Region and
is not a replication mechanism or hot-path dependency. Restore creates a new
database, key version, and verified frontier before traffic moves. A healthy
multi-Region database has a five-minute target to recover routing after loss
of one Region; PITR restore has a size-dependent target and uses the durable
operation model in ADR 0028.

## Operability

The launch posture is a waitlist beta after the required seven-day soak and
restore, fault, and degraded-mode checks. The Orchestrator owns placement,
drain, rollout, replacement, key rotation, backup lifecycle, and repair. The
dashboard and private Prometheus/Grafana path are the operator surface.
Metrics use only bounded labels under ADR 0036; per-database and per-tenant
numbers are usage facts, not metric labels. Paging is reserved for
customer-visible SLO burn.
