---
status: accepted
---

# Independent Rust engine and native storage format

TerraBaseV2 is an independent Rust database engine with a native storage
format. It does not embed, port, open, import, or translate RavenDB or
TerraBase V1 storage.

The engine owns versioned WAL, segment, manifest, CRDT, causal, snapshot,
backup, index, and recovery formats. Every persisted artifact names its format
major, required features, database and Partition identity, integrity digest,
and encryption identity where applicable. Unknown majors and required features
fail closed. A future V2 format upgrade needs an explicit crash-safe upgrade or
rewrite contract and recovery evidence.

V2 starts with empty native databases. No V1 migration, dual write, change
tail, compatibility reader, rollback reader, or Smuggler path exists.
