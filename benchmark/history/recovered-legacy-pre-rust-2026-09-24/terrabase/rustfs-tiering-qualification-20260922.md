# RustFS and storage-tier qualification — 2026-09-22

TerraBase now has an injected S3-backed immutable segment store with a
verified SSD cache. **100-million-record and bounded-memory qualification is
still incomplete.** Small successful RustFS runs do not establish that scale.

## Implementation exercised

Segments are uploaded under content-addressed names before manifest
publication. `MANIFEST` records whole-file SHA-256 and length. Streamed
downloads are bounded and checked against those bindings and the segment
footer before cache admission. Cache hits are verified too. Read/backup
leases pin files; writes and lease release trim the idle cache. Remote objects
are retained by default.

The idle cache budget excludes active pins and staging space. A backup capture
pins remote object retirement while releasing verified local segment copies
after each checksum cut; later checks refetch one segment at a time. WAL and
manifests remain local. The qualification runs described below materialize
restored generations locally; they do not exercise the separate offline
fresh-identity S3 restore path.

The inspected ZoneTree fork provides local multipart segments and cache
controls. Its live-backup interface is not a remote ordinary-read provider.
This tier uses TerraBase's segment interface. Further changes moved receipts
to a rebuildable ZoneTree index, streamed receipt recovery, and released state
on disposal. The segment qualification records the direct tier path; backup,
restore, and snapshot memory limits are reported by their own workloads.

## Fixture

| Property | Value |
| --- | --- |
| Runtime | RustFS 1.0.0 in Podman |
| Container | `terrabase-rustfs-20260922` |
| Endpoint | `http://127.0.0.1:19000` |
| Image digest | `sha256:ba0a1b53e36f321c0d46f3867104abef169f7bc59c467c664ddac87e7ddc9a8b` |
| Reported revision | `d47f54bfb2f39f48bd1adda334bd27e151fe85b8` |
| Object data | Ephemeral; no host-power-loss qualification |

Credentials come from environment variables and are absent from receipts.
The native adapter stores whole-object SHA-256 metadata because RustFS's
multipart checksum may be absent or composite. Tier downloads independently
verify actual bytes against the engine manifest.

## Historical volume comparisons

These runs used an earlier dirty source tree at commit `30c5c6fa`; they do not
qualify throughput or memory for the current worktree. Each wrote 1,100,000 records with a repeated-character 128-byte
payload, synced WAL batches of 1,000, four remote segments, and a 16 MiB idle
cache budget. Every key/body/version was checked after deleting all local
segment copies. These are shared-host observations without a cgroup envelope.

| Implementation | Seed | Writes/s | Compact/upload | Remote reopen + exact scan | Peak working set |
| --- | ---: | ---: | ---: | ---: | ---: |
| [v2: resident receipts](../bench/rustfs-tier-1100k-20260922-v2/tiered-rustfs-summary.md) | 26.960 s | 40,801 | 38.942 s | 52.100 s | 7,111,319,552 B |
| [v3: first receipt spill index](../bench/rustfs-tier-1100k-20260922-v3/tiered-rustfs-summary.md) | 65.641 s | 16,758 | 43.578 s | 95.402 s | 6,470,004,736 B |
| [v4: binary receipts and streaming recovery](../bench/rustfs-tier-1100k-20260922-v4/tiered-rustfs-summary.md) | 51.037 s | 21,553 | 39.742 s | 60.780 s | 2,553,036,800 B |
| [v5: final 32 MiB receipt write buffer](../bench/rustfs-tier-1100k-20260922-v5/tiered-rustfs-summary.md) | 28.603 s | 38,457 | 37.433 s | 49.305 s | 2,508,857,344 B |

v3 reduced peak RAM but regressed throughput. Binary receipt encoding,
streaming recovery, restored-body shedding, and tuning the receipt write
buffer recovered most throughput. v5 uses single-file ZoneTree cache segments
so maximum-size receipt values do not overflow multipart boundary metadata.
Peak working set fell by about 65% versus v2; seed throughput is about 6%
lower, and total elapsed time is 115.988 s versus 118.520 s. These are single
shared-host runs, not statistically established speedups. Earlier 10k/100k
smoke runs used another tier implementation and async WAL and are not comparable.

## Current qualification ladder receipts

The current direct tier runner has a passing 100,000-record RustFS receipt with
exact scans, point reads, updates, deletes, compaction, and empty-cache remote
reopen verification:

| Rung | Summary | Detailed receipt | Resource envelope |
| --- | --- | --- | --- |
| 100,000 records | [tiered RustFS summary](../bench/receipts/tiered-rustfs-100k-20260923T000001Z/tiered-rustfs-summary.md) | [tiered receipt](../bench/receipts/tiered-rustfs-100k-20260923T000001Z/tiered-rustfs-receipt.json) | [resource envelope](../bench/receipts/tiered-rustfs-100k-20260923T000001Z/resource-envelope.json) |

This was an uncapped shared-host run (`memory.max=max`, `memory.swap.max=max`)
with a 429,469,696-byte process peak working set. The cache was already empty:
both eviction counts and observed cache bytes were zero. It proves a remote
reopen with no local segment copy, not an observed cache eviction or a memory cap.

No passing 256 MiB cgroup qualification is available. The [256 MiB run
receipt](../bench/larger-memory-cgroup-20260922T234546Z/larger-memory-summary.md)
failed while seeding 150,000 records with 2,048-byte payloads, so it remains a
capacity failure boundary rather than evidence of bounded larger-than-memory
operation. A later [40,000-record diagnostic](../bench/larger-memory-state-map-budget-v9-incompressible-40k-8k-noswap-20260923T011447Z/diagnostic-stop.md)
with 8 KiB per-key incompressible payloads reached 1.1 GiB of local storage
inside a 256 MiB, zero-swap cgroup without an OOM kill. It was stopped during
its first compaction after 16 minutes, before producing a passing receipt.
The 100-million-record rung is still pending.

A current-build 1.1-million-record attempt was [stopped at its 100,000-record
checkpoint](../bench/receipts/tiered-rustfs-current-1100k-20260923T002052Z/tiered-rustfs-aborted.md).
It ran alongside normal-priority builds and tests, so its 241.327-second seed
interval is not a clean throughput comparison. A live stack sample found
repeated compressed state-index reads while appending new physical sequences.

## Full function checks

The [100,000-document tiered RustFS function receipt](../bench/receipts/e2e-rustfs-100k-20260923T001023Z/e2e-volume-summary.md)
uses `--object-tier s3`, compacts the source to RustFS, and deletes its SSD
segments before reopen. Backup artifacts also use RustFS.

| Check | Result |
| --- | --- |
| Seed / exact scan | 100,000 deterministic random documents |
| Point reads / active indexed queries | 500 / 100 |
| Updates / deletes | 1,000 version-2 documents / 1,000 tombstones |
| Full / incremental backup | 34.247 s / 26.192 s |
| Full-boundary / PITR / final restore | 26.664 s / 27.110 s / 25.600 s |
| Final exact scan | 99,000 matches; 1,000 deleted IDs absent |
| Compaction and reopened-engine verification | Passed |

This 100,000-document run disabled relationships. The earlier [10,000-document
RustFS receipt](../bench/rustfs-tier-e2e-20260922-v3/e2e-volume-summary.md)
verified related listing, traversal, and restrict-delete behavior through
restores with 100 users and orders.

A [40-document RustFS function receipt](../bench/e2e-paging-rustfs-agent-root-20260923/e2e-volume-summary.md)
also passed relationship writes, traversal, restrict-delete, collection
listing, snapshot document pagination, full/incremental/PITR restore, and a
cold remote reopen after evicting restored local segment copies. Its scale is
a functional smoke test, not volume evidence.

These qualification receipts materialize restored generations locally. Current
source behavior distinguishes that receipt scope from restore activation:
same-identity S3 activation remains fenced until distributed writer fencing is
available, while an offline fresh-identity restore can allocate a new database
namespace, publish its remote generation authority, and reopen after local-root
loss. Focused restore tests cover the fresh-identity path, but backups that
contain automatic-index decisions are rejected there pending destination-local
rebuild. Pre-activation crash recovery, concurrent destination-root reservation
hardening, provider-independent failure domains, and full function correctness
at 100 million records remain unqualified.

## Failure and scale boundaries

Tests cover upload outages, valid-but-wrong remote objects, corrupt cache,
oversized responses, cancellation, concurrent leases, backup capture, and
checkpoint persistence. Three process tests terminate before upload, after
upload before manifest publication, and after compaction; each recovers and
recompacts with local segment copies discarded. Those deterministic process
tests use a filesystem object-store fixture.

The [real filesystem-full receipt](../bench/disk-full-20260922-v2/disk-full-summary.md)
uses a dedicated 16 MiB tmpfs in a private mount namespace. WAL append and
segment staging both reported ENOSPC (`0x1C`). All 100 acknowledged bodies
and versions survived. After freeing space, the unacknowledged request was
recovered as committed with its exact body and matching receipt; this confirms
why a failed append must be treated as an unknown outcome. Failed compaction
left the manifest byte-for-byte unchanged, and retry plus another reopen
passed. The object store was a filesystem fixture outside the quota. This
does not establish RustFS disk-full or device power-loss behavior.

The final Release solution build passed with zero warnings and errors. The
full TUnit suites passed 509 core tests (four Windows-only skips) and 1,444
API tests (two skips), with zero failures. Focused post-fix suites included
the compaction merge fast path (6/6) and native restore targets (14/14).
The three process crash cases and filesystem-full drill are separate receipts.

Remaining work includes a passing bounded-memory run with incompressible
payloads, a clean throughput ladder after the append-index probe fix, scalable
large-history restore and incremental backup, working-space accounting,
remote-provider-full and multipart process-resumption drills, and the actual
100-million-record run with full-function verification.
Throughput extrapolation and S3 backups cannot substitute for those checks.
