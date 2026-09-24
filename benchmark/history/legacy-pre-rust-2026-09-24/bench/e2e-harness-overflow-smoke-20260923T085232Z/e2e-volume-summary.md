# TerraBase end-to-end volume qualification

Status: **complete** — 2026-09-23T08:52:38.8805957+00:00

17 deterministic random documents written in batches of 2,147,483,647, verified by exact read-back, paginated snapshot listing, and point reads, backed up (full + incremental around 1 updates and 1 deletes), restored three times (full boundary, interior PITR, final chain), queried through an active index, compacted, and verified again on reopened engines.

| Phase | Elapsed | Note |
| --- | ---: | ---: |
| Bulk write | 508 ms | 33 docs/s |
| Relationship write | 0 ms | 0 orders + users through the schema store |
| Relationship verify | 0 ms | 0 endpoints; no relationships configured; traversal, restrict delete |
| Read-back verify | 20 ms | 17 exact matches |
| Point reads | 0 ms | skipped |
| Full backup | 473 ms | artifact 3,252 B |
| Bulk updates | 4 ms | 1 documents to v2 |
| Bulk deletes | 4 ms | 1 tombstones |
| Incremental backup | 321 ms | artifact 4,593 B |
| Full-boundary restore | 519 ms | 0 log entries |
| Full restore verify | 3 ms | 17 exact matches |
| PITR restore | 379 ms | 1 log entries |
| PITR restore verify | 4 ms | 17 exact matches |
| Final restore | 327 ms | full incremental chain |
| Final restore verify | 3 ms | 16 exact, 1 absent |
| Snapshot document pages | 15 ms | 17 exact documents |
| Snapshot collection pages | 8 ms | 1 collections |
| Cold RustFS reopen | 0 ms | 0 exact, 0 collections, 0 segment objects, SSD cache 0 B, restored local segments 0 B |
| Root-loss recovery | skipped | opt-in flag disabled |
| Index build | 0 ms | automatic index on sequence, active |
| Indexed queries | 0 ms | skipped |
| Compaction | 71 ms | 18 records, 1 tombstones |
| Compaction verify | 5 ms | 16 exact matches |

Full artifact plaintext SHA-256: `5ec9becf921e34f6152065a6192cd44997cfdfa39210d33b8d4a698b99a1dcf0`
Incremental artifact plaintext SHA-256: `3cb11e27345bc2786fad434b90c641f338a63009204c5892c9a97959cdec170c`
Indexed query diagnostics: skipped.