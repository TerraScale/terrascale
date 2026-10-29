# 15. Catalog (`_system`) as a lattice database

`_system.db` (databases, saved queries, backup policies, usage, dashboard documents, management commits) uses the same
layout, write path and streams. It is placed in **every** region, so every machine holds it and serves catalog reads
and writes locally. There is no strong reason to keep a primary. The costs, all within the plan's eventual-consistency
contract:

- **Unique names** (database per project, and the like): checked locally. A concurrent duplicate across regions
  resolves deterministically through the unique-constraint loser rule (the smallest ULID wins). The loser's
  `/operations/{id}` ends `failed: name_taken`, and its database is deleted.
- **Limits** (databases per project, plan limits) are checked against local state, so they can briefly overshoot by
  one concurrent create per region.
- **Usage totals** become `inc` counters, so every machine's counts sum without forwarding (`CatalogPrimaryClient`
  goes).
- **Durability:** every `_system` commit syncs its write-ahead log ([0012](../decisions/0012-system-database-synchronous-full.md)),
  so an acknowledged key, revocation or registration survives a power loss of its machine.

Deleted: `CatalogFeed`, `CatalogFollower`, `CatalogForwarder`, `CatalogPrimaryClient`, `ForwardedRequests`,
`catalog.proto`, `ISystemReplication`, `SqliteStorageTransaction.System*`, `SqliteStorageEngine.System`,
`JournalDigest`, `VersionClock`, the system schema, `PRIMARY_REGION` and `CatalogFollower`, and PR #11's "one machine
in the primary region" shortcut. Decision 0009 is superseded.
