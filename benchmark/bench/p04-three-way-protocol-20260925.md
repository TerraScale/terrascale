# P04 embedded SQLite, Turso Database, and TerraBase diagnostic

Registered before the first three-way run. This is a focused diagnostic, not
an amendment to the TerraBase v1 acceptance matrix or its eight-pair P04
protocol.

Each lane uses the same static `p04-three-way` executable and a fresh pinned
Firecracker VM (4 vCPUs, 8,192 MiB, guest swap off) with a fresh database path.
The fixture has 1,024 integer keys, one-byte deterministic payloads generated
from seed 1, all rows deleted, and an explicit checkpoint after deletion.
SQLite and Turso use embedded primary-key SQL tables, WAL, `synchronous=FULL`,
and a 64-MiB page cache. TerraBase uses local-durable commits, its 128-MiB
process admission budget, and one immutable run after checkpoint. A measured
operation requests the first ordered row with a limit of one and must return
empty. Each VM performs 1,000 scans: operation 0 is first-after-fixture;
operations 1–999 are warm repeats. Each operation has a raw JSONL sample and
each lane has a receipt with executable, dependency lock, fixture, and guest
manifest hashes.

Run three complete VM blocks in these preassigned orders:

| Block | Order |
| --- | --- |
| 01 | SQLite → Turso → TerraBase |
| 02 | Turso → TerraBase → SQLite |
| 03 | TerraBase → SQLite → Turso |

Keep every failed run and its receipt. For each successful lane, report its
first-scan latency and nearest-rank warm p50, p95, and p99. For a descriptive
three-block comparison, divide the sum of the three per-VM endpoint values
for one engine by the corresponding sum for SQLite. Do not pool the 999 warm
operations across VMs, discard outliers, or use three blocks for a v1
acceptance verdict. Report the physical fixture, disk bytes, cache policy,
durability setting, host/cache limits, source and binary hashes, and all run
IDs. Turso is pinned to Rust crate 0.7.2 with default features disabled.

The current harness does not qualify upstream guest image provenance,
exclusive host CPUs, a reset host page cache, or equal whole-guest memory
consumption. Results are therefore exploratory even if all nine lanes pass.
