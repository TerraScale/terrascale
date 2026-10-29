# Durable sync revision prototype (W0-2)

Spike I.2 found that a Turso page-sync revision is just the WAL frame number. After `wal_checkpoint(TRUNCATE)` the numbering restarts, and the official client's `pull()` returns `false` while rows are still missing. This prototype fixes that with a durable revision, and qualifies it with the official JS client through checkpoint, restart, crash, rewrite and restore. It is the base for task R1.

Results: [`benchmark/bench/2026-10-29-durable-revision.md`](../bench/2026-10-29-durable-revision.md).

## The revision contract

A revision is `ts1:<incarnation>:<generation>:<frame>`. The client stores it as an opaque V1 string.

- **Incarnation** is a ULID. A new one starts on restore, or when the manifest is missing or fails its checksum (D18).
- **Generation** goes up by one each time the WAL is sealed: a coordinated checkpoint, a rewrite, or a WAL restart the host did not start.
- **Frame** is the committed WAL frame inside that generation.

Before the host truncates the WAL, it durably records which pages changed in the generation and the last frame that wrote each page. It writes the manifest to a temp file, fsyncs, renames, and fsyncs the directory. Only then does it truncate (`CheckpointMode::Truncate` bounded to the sealed frame). A pull from `(g, f)` gets every page changed after `f` in `g` and in every later generation, read at one WAL watermark, plus the current `db_size`. Pages are always current, so the answer is a superset of what changed and is never stale. A crash between the manifest write and the truncate only makes later answers larger.

| Client revision | Answer |
|---|---|
| Empty (bootstrap) | Every page (`full;bootstrap`) |
| Same incarnation, generation still retained | Changed pages only (`incremental`), or nothing (`empty`) |
| Generation compacted into a horizon bucket | The bucket's page set plus newer generations (`incremental`) |
| Older than the horizon | Every page (`full;expired`) |
| Sealed as full: rewrite, or continuity not provable at startup | Every page (`full;rewritten`) |
| Other incarnation (restore) or unparsable | Every page (`full;other-incarnation`, `full;unrecognized`) |
| Ahead of the durable history (frames lost in a crash), including inside a compacted bucket | Every page (`full;ahead`) |
| Pinned `server_revision` (chunked bootstrap) not current | HTTP 409 `revision_expired` |

A full answer is a normal incremental page stream that covers every page up to `db_size`. The page-mode client applies it as a rebootstrap: it rolls back its local changes, applies the pages, and replays its pending offline writes. The client rejects `ReplaceBase` in page mode, so the host never sends it.

History stays bounded:

- **WAL.** The policy is checked after every pipeline item (one execute or one batch), and a checkpoint runs once the WAL reaches 1,024 frames (4 MiB). The WAL never exceeds that plus the largest single item.
- **Exact history.** The last 8 sealed generations keep per-page last frames.
- **Compacted history.** Older generations fold into up to 8 buckets of 8 generations each, stored as roaring page sets plus each generation's last frame. Clients at most 64 to 72 generations behind still get an incremental answer; older ones get a full rebootstrap.

## How the host covers each lifecycle event

| Event | Handling |
|---|---|
| Checkpoint | Coordinated under the database's writer lock: seal, persist, then truncate. Runs on the 1,024-frame policy and on admin request. |
| Checkpoint through SQL | Denied: the pipeline rejects PRAGMA, VACUUM, ATTACH and DETACH per statement (`SQL_DENIED`). Checkpoints run only through the coordinator. As defense in depth, a WAL `checkpoint_seq` change the host did not cause seals the generation as full. |
| Transaction left open | A request that ends inside a transaction is rolled back and gets HTTP 400 `transaction_left_open`. Every request therefore ends in autocommit, the page index is refreshed after every item, and one client's open transaction can never absorb another client's write. |
| Change while the host was down | At startup, the manifest's WAL salt and database file identity (size, inode, mtime) must match what the host recorded after its last checkpoint or close. If either differs, the generation is sealed as full. |
| Graceful restart | SIGTERM drains requests, then seals and checkpoints every open database. |
| Crash | On open, WAL frames are indexed into the current generation, then a startup checkpoint seals them. Every startup fsyncs the data root and all its ancestors, so an interrupted directory creation is completed. Tenant directories and the manifest directory are fsynced too. |
| Manifest write fails | The database is fenced: every later pipeline, pull and checkpoint fails until a restart reloads the last durable manifest. No revision is ever issued from history that was not persisted. |
| Rewrite | `VACUUM INTO` a staged file. The generation is marked full in the manifest before the file swap, so a crash in between stays safe. |
| Restore | A new incarnation is written before the file swap. Every client rebootstraps (D18). |

## Layout

| Path | What it is |
|---|---|
| `host/` | Rust binary. `src/main.rs` compiles upstream `cli/sync_server.rs` from the pinned tarball as a module, and reuses its Hrana pipeline and HTTP helpers. `revision.rs` holds the revision format, manifest and pull planning. `tenant.rs` holds the per-database writer gate and lifecycle. `server.rs` holds routing, per-database bearer tokens and test-only admin routes. |
| `build.sh` | Downloads the pinned Turso source (SHA-256 checked) and builds the host and the control `tursodb` with Rust 1.88. It fails if `Cargo.lock` changes beyond the added host package. |
| `qualify/` | Official `@tursodatabase/sync` 0.8.2 harness. `run.sh` runs the scenarios plus the upstream control and writes results and a receipt. `report.mjs` renders the results page. |

## Reproduce

Heavy builds and runs go through `ts-heavy-any`:

```sh
ts-heavy-any bash -c 'nice -n 19 benchmark/turso-sync-host/build.sh && nice -n 19 benchmark/turso-sync-host/qualify/run.sh "$HOME/w02.json" 3' --repo terrascale --branch <branch>
node benchmark/turso-sync-host/qualify/report.mjs w02.json w02-receipt.json > benchmark/bench/<date>-durable-revision.md
```

The host takes `--listen`, `--data` and `--tokens <file>`. The token file is JSON: `{"admin": "...", "databases": {"<name>": "..."}}`. The harness generates random tokens for each repeat. Bind to loopback only.

## What R1 still has to build

- Run the coordinator inside TerraBase's writer admission. Every checkpoint path must go through it: shutdown, rewrite, copy, restore and backup replay.
- Stream full answers instead of buffering them in memory.
- Store the page index outside one JSON manifest for large databases.
- Replace this small statement filter with the full D15 sandbox.
- Qualify the other SDK languages.

See [NOTICE.md](NOTICE.md) for the upstream license.
