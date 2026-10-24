# Free replication: log chunks through the object store, per 1k replicated changes (2026-10-23)

RT3b. Bytes sent over the peer links, bytes put into the object store, and object-store requests per 1,000 replicated
changes. Three regions, three repeats, measured against an inline-only baseline. Also an estimated monthly cost per
1M writes at R2 and OCI list prices.

Replication is never billed to customers. The Fast test `FreeReplicationUsageTests` checks that billed reads, writes and
operation units count only the serving region's client operations. Peer applies, the refill of a region that joins later
and anti-entropy rounds appear only in the replication analytics (#519).

## Results

Each cell is the median of 3 repeats, with the range (min–max) in parentheses. Bytes and requests are summed over the
three regions and divided by changes / 1,000.

| Scenario | Shipping | Peer-link bytes / 1k changes | Object-store bytes / 1k changes | PUTs / 1k | GETs / 1k | USD per month per 1M writes | Load seconds |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| small | inline only (no bucket) | 2,212,363 (2,205,101–2,229,492) | 0 (0–0) | 0.00 (0.00–0.00) | 0.00 (0.00–0.00) | $0.031 ($0.031–$0.031) | 84.8 (82.0–120.6) |
| small | log chunks (bucket) | 2,218,580 (2,214,703–2,226,809) | 0 (0–0) | 0.00 (0.00–0.00) | 0.00 (0.00–0.00) | $0.031 ($0.031–$0.031) | 98.8 (98.0–113.5) |
| bulk | inline only (no bucket) | 6,251,138 (6,250,724–6,252,927) | 0 (0–0) | 0.00 (0.00–0.00) | 0.00 (0.00–0.00) | $0.088 ($0.088–$0.088) | 20.3 (17.8–24.0) |
| bulk | log chunks (bucket) | 1,668,152 (1,559,066–2,005,973) | 2,568,493 (2,491,004–2,610,386) | 2.40 (2.30–2.90) | 4.00 (3.80–5.10) | $0.074 ($0.071–$0.075) | 24.6 (19.4–24.8) |

- small: egress bytes (peer + store uploads) +0.3% with chunks; monthly cost per 1M writes +0.3% (medians).
- bulk: egress bytes (peer + store uploads) -32.2% with chunks; monthly cost per 1M writes -15.0% (medians).
- **Small writes.** Single 1 KiB writes never make a chunk. Batches stay far below the two-reader break-even of 209 KB
  from `gru` and 614 KB elsewhere. Bytes match the baseline within 0.3%, and there are no object-store requests.
- **Bulk writes.** Under bulk writes (transactions of 50 × 3 KiB), both other regions trail the writer, and large
  stretches of the log go through the bucket:
  - Egress (peer links plus the store uploads) falls by about a third.
  - The estimated cost falls by 15%: PUTs (class A) take part of the saving.
  - Each chunk is put once and read 1.7 times on average (4.00 GETs for 2.40 PUTs per 1k changes). The other reads went
    inline: a region that had already passed the chunk, or one that was the only reader.
- **Load time.** The same within run-to-run noise, as the range of the small-write baseline shows.

Per region (median of the repeats). Bytes and requests are the region's own:
- Peer-link and store bytes are those it shipped as a sender.
- GETs are those it read as a receiver.

| Mode | Scenario | Region | Peer-link bytes | Object-store bytes | PUTs | GETs |
| --- | --- | --- | ---: | ---: | ---: | ---: |
| bucket | bulk | gru | 4,888,978 | 9,621,950 | 10 | 14 |
| bucket | bulk | sjc | 6,336,404 | 7,800,052 | 6 | 16 |
| bucket | bulk | fra | 5,015,961 | 8,995,912 | 8 | 14 |
| bucket | small | gru | 7,454,038 | 0 | 0 | 0 |
| bucket | small | sjc | 7,380,526 | 0 | 0 | 0 |
| bucket | small | fra | 7,360,171 | 0 | 0 | 0 |
| none | bulk | gru | 20,855,544 | 0 | 0 | 0 |
| none | bulk | sjc | 20,831,760 | 0 | 0 | 0 |
| none | bulk | fra | 20,829,984 | 0 | 0 | 0 |
| none | small | gru | 7,422,710 | 0 | 0 | 0 |
| none | small | sjc | 7,356,230 | 0 | 0 | 0 |
| none | small | fra | 7,344,691 | 0 | 0 | 0 |

## Cost model

The rule `LogChunks.Shipping.cs` applies is built from code constants: R2 list prices and OCI outbound transfer
(developers.cloudflare.com/r2/pricing, OCI price list, both read 2026-10-23).

- R2: PUT (class A) $4.50 per million, GET (class B) $0.36 per million. Egress and deletes are free.
- OCI outbound transfer past the free 10 TB a month: $0.025/GB from South America (`gru`), $0.0085/GB from `sjc` and
  `fra`. Downloading from R2 is inbound to OCI, which is free.
- Monthly cost per 1M writes = (Σ over regions of (peer bytes + store bytes) × that region's egress price, + PUTs × $4.50/M
  + GETs × $0.36/M) × 1,000,000 / changes.
- Chunk storage is left out. A chunk lives at most 10 minutes: about 2.5 MB per 1k changes for 10 minutes, under
  $0.0001 a month per 1M writes at $0.015/GB-month.
- Within OCI's free 10 TB a month and R2's free tier (1M class A, 10M class B), the marginal cost of either path is $0.
  The saving matters only past those allowances.

## Setup

- **Host and commit.** `mariogk-dev` (46 CPUs), through `ts-heavy-any`, branch `feat/rt3b-free-replication-bench` at
  `cf68db2c`. That commit is the log chunks code of the shipping PR plus this benchmark.
- **Cluster.** `make benchmark-repl-chunks` (`benchmark/scripts/repl-chunks.sh`) runs `BenchmarkClusterHost`: gru, sjc
  and fra as local nodes with the real routes' one-way link delays (gru–sjc 85 ms, gru–fra 94 ms, sjc–fra 69 ms).
- **Modes.** Two modes, each with its own host:
  - **bucket:** the nodes share one in-process S3 bucket, as production nodes share the platform bucket.
  - **inline:** no bucket, the baseline.
- **Load.** `repl-bench --docs 10000 --concurrency 16 --conflict-docs 0 --probe-every 0` writes across all three regions:
  - **small:** one 1 KiB document per PUT;
  - **bulk:** transactions of 50 documents of 3 KiB.
- **Measurement.** Every region's `/metrics` is scraped before and after each load:
  - `terrabase.replication.shipped_bytes` by `path` (`peer`: inline batches and chunk references; `store`: sealed chunks
    put);
  - `terrabase.replication.log_chunk_requests` by `operation`.

## Not measured

- **Object-store latency and real bucket behaviour.** The bucket is an in-process S3 fake, so request counts and bytes
  are exact, but timings are not R2's. The real-bucket path (Tigris and R2, configuration only) is covered by the Slow
  test `TigrisLogChunkTests`, not timed here.
- **Other replication traffic.** Only change-group batches are counted. Heartbeats, frontiers, refills and anti-entropy
  are not.
- **Deletes.** Each chunk is deleted 10 minutes after it was put, outside the measurement window. Deletes are free on R2.
- **Real cells.** These are local nodes with emulated delays: no NetBird, no OCI egress meter. The bytes are what TerraBase
  hands to the transport.
