# Why TerraScale runs on SQLite (stub)

> L-5 launch-post stub. Full post lands with the benchmark numbers (E-13) and
> the storage decision record (D-27).

- Mainline SQLite via `rusqlite`, one file per database, WAL mode, one writer.
- Publication journal folded into the documents table; history = journal retention (D-42).
- What this post will show: why the most battle-tested storage engine on earth
  beats a custom LSM for our workload, and what our multi-region CRDT sync adds on top.
