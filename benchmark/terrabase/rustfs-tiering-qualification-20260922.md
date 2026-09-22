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

The idle cache budget excludes active pins and staging space. Backup pins all
captured segments. WAL and manifests remain local. Restored generations are
local, and the default host factory does not create remote-tier stores.

The inspected ZoneTree fork provides local multipart segments and cache
controls. Its live-backup interface is not a remote ordinary-read provider.
This tier uses TerraBase's segment interface. Further changes moved receipts
to a rebuildable ZoneTree index, streamed receipt recovery, and released state
on disposal; live documents/history and several whole-corpus operations still
remain memory-resident.

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

## Comparable volume runs

Each run below wrote 1,100,000 records with a repeated-character 128-byte
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

## Full function checks

The [latest tiered RustFS function receipt](../bench/rustfs-tier-e2e-20260922-v3/e2e-volume-summary.md)
uses `--object-tier s3`, compacts the source to RustFS, and deletes its SSD
segments before reopen. Backup artifacts also use RustFS.

| Check | Result |
| --- | --- |
| Seed / exact scan | 10,000 deterministic random documents |
| Point reads / active indexed queries | 500 / 100 |
| Updates / deletes | 100 version-2 documents / 100 tombstones |
| Relationships | 100 users/orders; related listing, traversal, restrict-delete verified through restores |
| Full / incremental backup | 1.420 s / 1.463 s |
| Full-boundary / PITR / final restore | 1.307 s / 0.786 s / 0.725 s |
| Final exact scan | 9,900 matches; 100 deleted IDs absent |
| Compaction and reopened-engine verification | Passed |

Restored generations are local. This does not establish remote-tier restore
activation or full function correctness at 100 million records.

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

Validation also passed 483 core tests (four Windows-only skips), all 1,337 API
tests before the final cache-layout/buffer adjustment, 66 focused storage
tests after the layout correction, five receipt-index tests at the final
buffer size, and three process crash cases. The final Release benchmark build
had zero warnings and errors.

The earlier [256 MiB cgroup run](../bench/larger-memory-cgroup-20260922T1740Z/larger-memory-summary.md)
failed while seeding 150,000 records with 2,048-byte payloads. This failure
predates the receipt changes; it is not a successful capacity receipt.

Remaining work includes disk-backed document/revision/history indexes,
bounded snapshots and compaction/backup capture, host-tier composition,
working-space accounting, remote-provider-full and multipart process-resumption drills,
and a measured workload ladder ending with the actual 100-million-record run.
Throughput extrapolation and S3 backups cannot substitute for those checks.
