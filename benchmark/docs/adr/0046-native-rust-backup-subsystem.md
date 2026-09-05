# ADR 0046: Native Rust backup subsystem informed by Litestream

Status: accepted (owner decision, 2026-09-05).
Authority: workspace `GOAL.md`, D-29 and E-7. This decision replaces the
external Litestream implementation choice in ADR 0040 and applies to backup,
restore, branching, and bootstrap references in earlier ADRs.

## Context

TerraBase currently supervises a Litestream child process per streamed
database in `crates/terrabase-server/src/backup.rs`. Our member hosts many
SQLite databases and already owns WAL capture, checkpoint scheduling,
encryption, and database lifecycle. Separate backup processes repeat runtime
and scheduling resources and cannot directly share these engine facilities.
The size of that overhead and the benefit of replacing it require measurement.

## Decision

Rebuild the required Litestream functionality as an internal native Rust
subsystem of TerraBase. Read and study
[Litestream's source](https://github.com/benbjohnson/litestream) before
implementing the replacement. Record the reviewed commit, source paths,
relevant tests and issues, recovery invariants, and deliberate departures in
an implementation design note. Cover WAL capture and checkpoint coordination,
snapshot and incremental formats, upload recovery, retention, and PITR.
Preserve applicable license notices and attribution for reused material.

The subsystem runs inside the Rust member. It has no external Litestream
binary, embedded Go runtime, per-database subprocess, or upstream runtime
dependency. Use established Rust crates for supporting facilities where
appropriate. Sharing proven ideas does not require retaining upstream's
deployment model or backup format compatibility.

Maintain one logical stream per SQLite file with per-database keys,
frontiers, resource accounting, and isolation. Share bounded background
workers, upload clients, scheduling, and reusable buffers across streams.
Integrate capture with the engine's WAL/checkpoint lifecycle so backup and
replication do not independently duplicate work where safe sharing is possible.
Backup owns a separate durable retention frontier: follower acknowledgement
does not permit deletion of bytes still needed for backup. Coordinate WAL
reuse with capture, preserve committed transaction boundaries, and persist
enough state to recover after a crash without gaps.

Object uploads remain asynchronous and outside the write acknowledgement and
reactive delivery paths. RF=2 follower fsync still determines
`RegionalDurable`; causal journal replication still connects Regions.
Bound queues, memory, staging disk, and upload concurrency. Specify and test
overflow and outage behavior; never silently discard unarchived data or
report a stale backup as healthy. Pause idle streams only after preserving
their pending backup state, release their active resources, and resume safely.

Preserve D-29: same-Region OCI Object Storage, 10-second sync interval and
10-second backup RPO target, per-database DEK encryption, PITR,
restore-to-new-database, branching, and bootstrap snapshots carrying a
consistent journal frontier. A configured interval alone does not prove RPO:
measure the latest remotely recoverable commit and expose lag/failure through
Prometheus. Object lifecycle remains orchestrator-owned: Infrequent Access
after 24 hours and Archive after 7 days. Retention must preserve complete
restore chains. Restores validate integrity before publication; Region
bootstrap tails peers and reconciles before serving, as in ADR 0044.

## Performance and correctness gates

Faster backup and restore, lower CPU and memory use, and less disk I/O and
object-request overhead are explicit implementation goals. Record comparative
Markdown tables in `bench/` against a pinned upstream Litestream version and
the existing per-database integration under equivalent durability, upload,
retention, and encryption settings. Include hardware, versions, workload,
database counts, and reproducible commands. Measure idle and active databases,
write-heavy load, startup/resume, backup throughput and lag, restore time,
member commit p99, RSS, CPU, disk bytes, and object requests. Report tradeoffs;
native Rust alone is not evidence of improvement.

Required recovery tests include crashes across capture/upload/publication
boundaries, torn or corrupt artifacts, interrupted and retried uploads, WAL
checkpoint/reuse races, object-store outages, retention boundaries, key
rotation/deletion, idle pause/resume, PITR, branching, and bootstrap with
frontier validation. Daily orchestrator restore drills remain required by
D-71. Correct recovery and isolation cannot be traded for benchmark gains.

## Implementation and consequences

This is an accepted design and required rebuild, not a completed implementation
or a measured performance claim. E-7 remains incomplete until the native path,
recovery evidence, and comparative benchmarks satisfy this decision.

In the replacement series, delete the external process adapter, binary
packaging, subprocess supervision, Litestream-specific runtime configuration,
and obsolete tests and documentation. Replace their coverage with native
integration and recovery tests. Keep no compatibility shim or permanent
fallback to the retired implementation. Source review and benchmark references
to upstream remain useful development material.

The orchestrator owns infrastructure, lifecycle intent, and restore drills;
TerraBase owns backup execution and readiness evidence. We take responsibility
for the backup format, recovery correctness, and maintenance of this subsystem.
