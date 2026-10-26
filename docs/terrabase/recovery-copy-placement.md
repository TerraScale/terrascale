# Recovery copy placement and snapshot routing

Branches, managed snapshot restores and point-in-time restores inherit the source
database's primary, explicit replica regions and `allRegions` choice. A copy starts
its own placement version at 1. Recovery never expands explicit placement to the
default region set, and a restored database consumes a database allowance unit
even when the snapshot lives outside its preserved primary region.

Managed snapshot records identify the region holding their local file. Restore
requests first authorize access and verify snapshot ownership, then answer 421
with `ts-replay: region=<snapshot-region>` when another region holds it. Replayed
requests do not emit another replay header. PITR runs in the source's primary.
Unknown snapshot regions return 503 `backup_region_unknown`; take a new snapshot.
If the snapshot region has left source placement, restore returns 409
`backup_region_not_placed` without registering a copy or widening residency.

Only the recorded snapshot region judges abandoned copies and sweeps their files.
Scheduled snapshots run at the database's primary. Replicas fill asynchronously;
a successful copy response does not establish that every replica is readable yet.

The nullable snapshot region is additive catalog metadata. Old nodes ignore it;
new nodes can list old records but refuse restores whose location is unknown.
Mixed-version nodes retain the old restore/sweep behavior until all are upgraded;
old sweep writers can drop the field from expiring records. No migration is added.
Needs format bump: the deployment lane owns any batched format/reset decision.
