# RT2 lane D: free replication (2026-10-23)

Branch `rt2/free-repl` (billing fix alone: `rt2/free-repl-1`, a16947f52), base origin/main 811251348.

## 1. Replication never bills customer usage

Billed units are the period reads and writes of `GET .../usage` (ts-dashboard `internal/adapters/terrabase/usage.go`)
plus the local copy's storage bytes (`UsageService.DatabaseAsync`, one copy, never multiplied by regions).

| Path | Records usage? | Evidence |
|---|---|---|
| Client document reads, writes, queries | Yes, once, in the serving region | `DocumentService.cs:166,174,274,485,502`, `QueryService.cs:67`: the only callers of `RecordRead`/`RecordWrites`/`RecordOperation` |
| Peer stream apply, anti-entropy, refill, rebuild copy | No | `Features/Replication/*` and `Engine/*` take no `IUsageMeter` and call no `DocumentService` |
| Shape and feed reads | Request count and body bytes only (not billed); no read operation | `GetShapeEndpoint.cs:214` counts `ShapeMetrics`, not `IUsageMeter` |
| Usage flush into the replicated catalog | **Leak, fixed** | below |

**Leak (fixed on `rt2/free-repl-1`).** Every region added its pending counts to the same replicated catalog documents
(`databases/<id>/usage/ops`, `usage-hours/<hour>`, `usage-days/<day>`, `usage-minutes/<minute>`) with a read-modify-write.
The catalog keeps the last write per field, so two regions flushing before each other's flush replicated kept one region's
counts and dropped the other's. The in-doubt settle also read the shared `flush` marker, so another region's marker could
make a node re-add a batch that had committed (a double count). Each node now writes only documents named by itself
(`ops~<node>`, `flush~<node>`, `<bucket>~<node>`); reads sum every node's documents, and a pre-existing unsuffixed document
still sums in. The proof writes in gru and sjc, refills fra, runs an anti-entropy round on all three, then flushes all
three at the same moment:

| Test (`tests/TerraBase.Fast/Replication/FreeReplicationUsageTests.cs`) | Old code | Fixed |
|---|---|---|
| `Each_client_operation_counts_once_in_its_region_and_replication_counts_nothing` | 3 of 3 runs fail: "Expected to be 8 ... but found 3" (twice), "found 5" | 3 of 3 pass locally; full Fast suite 2332 of 2332 on delta |

It also asserts fra (refill, streamed applies, anti-entropy, no client) has nothing pending and wrote no usage document.

## 2. Bytes on the wire per replicated write

`benchmark/scripts/repl-bytes.sh` runs three local regions in one process (BenchmarkClusterHost) and sums kernel TCP
`bytes_sent` of the host's peer sockets, sampled every 0.5 s (a socket's last count survives its close), client sockets
excluded, divided by two for the local PeerLink hop. Writes: `repl-bench`, PUT one document per request, 4 writers per
region spread over all three regions, no deletes or conflicts. Each write reaches two receiving regions. The idle rate
(130 s before the load; heartbeats, catalog feed, anti-entropy rounds) is subtracted. Peer streams are h2c here;
production adds TLS records (about 22 to 29 bytes per record).

Host `delta` (16 cores), commit 53c02efe, 2000 writes per run, 3 runs per size, a fresh cluster per run. "Raw" is every
peer byte during the load window divided by writes and by the two receiving regions (an upper bound: background traffic
included); "idle-subtracted" removes the rate measured in the 130 s before.

| Document | Raw B/write/receiver (median, runs) | Idle-subtracted (median, runs) | Idle peer B/s, whole cell (runs) | Load s (runs) |
|---|---|---|---|---|
| 256 B | **511** (496, 539, 511) | 487 (492, 183*, 487) | 1305, 65179*, 3965 | 12.6, 21.8, 23.9 |
| 4096 B | **3973** (4039, 3973, 3947) | 3941 (3955, 3925, 3941) | 19821, 3660, 1699 | 17.1, 52.6, 13.0 |

\* run 2 of 256 B caught a 65 KB/s burst in its idle window (catalog or refill traffic), which over-subtracts; the raw
figure is the one to plan with. A small write costs about 255 bytes of framing, ids, stamps and acks per receiving region;
a 4 KB write costs about its own size. The payload is random lowercase letters, which LZ4 barely compresses, so these are
close to the worst case; ordinary JSON compresses further. Idle traffic (median 3.8 KB/s for the three-node cell) is
about 10 GB a month.

## 3. Prices (fetched 2026-10-23 UTC)

| Item | Price | Source |
|---|---|---|
| OCI outbound, North America and Europe (sjc, fra) | first 10 TB/month free, then $0.0085/GB | OCI price list API part B88327 (`apexapps.oracle.com/pls/apex/cetools/api/v1/products/?currencyCode=USD`); https://www.oracle.com/cloud/networking/pricing/ |
| OCI outbound, South America (gru) | first 10 TB/month free, then $0.025/GB | same API, part B93455 |
| OCI inbound | free | https://www.oracle.com/cloud/networking/pricing/ ("Inbound Data Transfer Free") |
| Tigris Standard | $0.02/GB-month; Class A $0.005/1000; Class B $0.0005/1000; egress free; 5 GB free | https://www.tigrisdata.com/pricing/ |
| Cloudflare R2 Standard | $0.015/GB-month; Class A $4.50/million; Class B $0.36/million; egress free; free 10 GB-month, 1M Class A, 10M Class B per month | https://developers.cloudflare.com/r2/pricing/ (page "Last updated Oct 1, 2026") |

Peer traffic rides NetBird (WireGuard over public IPs), so it is billed as OCI outbound from the sending region. Uploads
from OCI to Tigris or R2 are OCI outbound too; downloads into OCI are inbound (free). Not verified: whether the 10 TB free
allowance is per tenancy or per region, and that it is shared with ts-auth and client response egress.

## 4. $/GB replicated, three regions (fan-out 2)

Per GB of change stream leaving the origin region. Object path: 4 MiB chunks (256 PUT + 512 GET per GB), retained 1 day,
chunk discovery signalled over the peer stream (no LIST polling: one LIST per second per reader would add about 5.2 M
Class A a month, $26 on Tigris, $23 on R2 before free allowances).

| Path | Origin under 10 TB/month | Origin sjc/fra over 10 TB | Origin gru over 10 TB |
|---|---|---|---|
| Peer streams (2 copies egress) | **$0** | $0.0170 | $0.0500 |
| Tigris chunks (1 upload + requests + storage) | $0.0022 | $0.0107 | $0.0272 |
| R2 chunks (1 upload + requests + storage) | $0.0018 | $0.0103 | $0.0268 |

At the measured worst case (539 B per receiver for a 256 B write, so 1,078 B of origin egress per write with two
receivers) the free 10 TB a month covers 9.3 billion small writes a month, about 3,600 writes/s sustained from each
geography; for 4 KB writes (4,039 B per receiver) it covers 1.2 billion, about 480 writes/s. sjc and fra share the North
America and Europe allowance. Idle peer traffic uses about 0.1% of it.

## 5. Decision

DECISION: keep peer streams for the live tail and for catch-up; do not ship replication log chunks through object storage
now. Reason: the origin's peer egress is free up to 10 TB a month (section 4), and below that the object path only adds
request and storage cost; above it, it saves at most $0.007/GB (sjc, fra) or $0.023/GB (gru). The object path also adds a
PUT, a notification and a GET to every change, which the p99 < 200 ms cross-region target cannot absorb on the hot path.

No replication log-chunk path exists in code today: `ContinuousBackup` ships one machine's physical row segments for
point-in-time restore (`BackupLayout.cs`, keys without replica, incarnation or HLC frontier), and `ShapeMirror` ships
encrypted shape pages. Design for bulk catch-up and rebuild when origin egress passes 10 TB a month (over 400 lines, so
not built here):

1. The origin seals ranges of `IChangeGroupSource` change groups into chunks keyed by database, origin `ReplicaId`,
   incarnation and change-id range, encrypted like backup objects, through the existing `S3ObjectStore`.
2. `PeerStreams` offers "chunks X to Y, then stream from Y" before a digest refill (today `Refill{ChangeId, Horizon}`);
   the peer stream carries the chunk keys, so readers never LIST.
3. Receivers feed chunk groups to `StorageChangeGroupSink` as joins and resume the stream at the chunk end; CDC retention
   counts "archived" like an ack.
4. Rebuild bootstraps from chunks and finishes with `AntiEntropy.RefillAsync`.
5. Rolling deploy: a new frame type old receivers ignore; the sender falls back to the refill when the receiver never
   asks for chunks.

## 6. Object store: Tigris proof, R2 switch

`scripts/terrabase/object-store-proof.sh <env-file>` runs one local Production node against a real bucket: it ships
snapshots, log segments, data keys and its catalog anchor (PUT), then restarts and lists and reads them back (LIST, GET).
Only the env file changes between providers (`AWS_ENDPOINT_URL_S3`, `AWS_REGION`, `BUCKET_NAME`, `AWS_ACCESS_KEY_ID`,
`AWS_SECRET_ACCESS_KEY`).

| Store | Bucket | Result |
|---|---|---|
| Tigris | `ts-rt2-free-repl-test` | 5 of 5 PASS (10 objects: 4 snapshots, 2 log segments, 3 data keys, 1 catalog anchor) |
| Cloudflare R2 | not created | blocked: the available Cloudflare token cannot manage R2 (403) |

R2 settings when unblocked: `AWS_ENDPOINT_URL_S3=https://<account-id>.r2.cloudflarestorage.com`, `AWS_REGION=auto`,
keys from an R2 token scoped to the bucket. OCI Vault names follow the cells' pattern: `<cell>-aws-endpoint-url-s3`,
`<cell>-bucket-name`, `<cell>-aws-access-key-id`, `<cell>-aws-secret-access-key`. Buckets: test `ts-rt2-free-repl-test`;
production `ts-terrabase-gru`, `ts-terrabase-sjc`, `ts-terrabase-fra` (one per region, location hints `wnam` for sjc,
`weur` for fra and `enam` for gru: R2 offers no South America hint).

## 7. Live measurement (lane B cells)

With the cells' targets file (`{"regions":{"gru":{"url":...,"token":...},...}}`) and NetBird SSH to each node:

```
nodes="<gru-node> <sjc-node> <fra-node>"
tx() { for n in $nodes; do ts-orch netbird ssh "$n" -- cat /sys/class/net/wt0/statistics/tx_bytes; done | paste -sd+ | bc; }
a=$(tx); sleep 130; b=$(tx)                       # idle rate = (b - a) / 130
c=$(tx); s=$(date +%s)
bin/repl-bench --targets cells.json --docs 2000 --sizes 256:100 --batch 1 --concurrency 4 --delete-pct 0 --conflict-docs 0 --probe-every 0
d=$(tx); e=$(date +%s)                            # bytes/write = (d - c - rate*(e - s)) / 2000
```

Not verified: the NetBird interface name (`wt0` is NetBird's default) and the `ts-orch netbird ssh` argument form.

## 8. Live cells (gru, sjc, fra; main 888ea413; 2026-10-23 03:00 to 04:30 UTC)

Lane B's test cells (`terrabase-rt2` units; gru on an E2.1.Micro VM). Test database `rt2laned` (all regions), created for
this lane so other lanes' probes on `rt2` do not touch its counts.

**Billing (old binary, without the fix).** `GET .../databases/<id>/usage` on each region after the runs below:

| Region | Documents | Writes billed | Reads billed |
|---|---|---|---|
| sjc | 7227 | 4812 | 132 |
| gru | 7227 | 4812 | 132 |
| fra | 7227 | 4812 | 132 |

Every document took at least one client write and none were deleted, so at least 2,415 writes (33%) were never billed:
concurrent flushes from three regions overwrote each other, as in section 1. Identical totals in every region show whole
flushes were overwritten. Replicated applies added nothing: no region bills more writes than there were. The fix
(`rt2/free-repl-1`) is not deployed on the cells.

**Bytes.** `benchmark/scripts/repl-bytes-live.sh` (kernel `bytes_sent` of each host's sockets on peer port 18081, sampled
every second; TCP payload only, NetBird's WireGuard headers come on top), 1000 writes per run from ts-sjc-1, 3 runs per size:

| Document | Idle-subtracted B/write/receiver (median, runs) | Raw (runs) | Idle peer B/s, cell (runs) | Load s (runs) |
|---|---|---|---|---|
| 256 B | **426** (339, 426, 472) | 971, 987, 1032 | 13243, 12091, 12697 | 95.5, 92.9, 88.2 |
| 4096 B | **4303** (4183, 4303, 4447) | 5007, 4953, 4900 | 11136, 13125, 14641 | 100.6, 99.0, 98.0 |

Other lanes' traffic on the cells (about 12 KB/s) is two thirds of a 256 B run's window, hence the spread. Live costs
match local within the noise for small writes; 4 KB writes cost about 9% more live (NetBird's 1228-byte MSS splits
frames into more segments, and `bytes_sent` counts retransmissions). The free-tier conclusion of section 4 holds.

**Links.** Kernel minimum RTT on the peer sockets: sjc-fra 130 ms, sjc-gru 168 ms, fra-gru 181 ms. One-way delivery fits
p99 < 200 ms on every link; anything that needs a round trip on fra-gru has about 19 ms left.

**Verify on gru.** repl-bench's verify listing fails on the gru VM with `400 invalid_query "query too expensive"` after
every run (the load itself completes and replicates: end markers reached every region within 24 s).
