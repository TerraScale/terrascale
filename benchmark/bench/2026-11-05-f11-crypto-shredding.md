# F11 crypto-shredding evidence — 2026-11-05

PIVOT Milestone B F11 / D19 is deployed as `91b84926d8c5d216a095b595d24715f7d613319c`. [CI 42079343306](https://github.com/TerraScale/terrascale/actions/runs/42079343306) is green, including timing and rollout. Rollout finished 18:05:32Z; all six founder/holder nodes were independently healthy and ready on this revision before and after the proof, using storage format `23-41a88901325a`.

The final DELETE returned **204 in 6.081s**. Every gru/sjc/fra read, backup, restore and shape-key probe then failed; source and snapshot custody keys returned 404 and local database files were absent. The previously cached CDN URL returned 404 in all three regions by **72.153s after 204**. The throwaway account closed in **2.654s**, with its private state and API-key files removed. All three previously reported pending accounts are closed.

All identifiers and rows below are synthetic. Raw artifacts contain response metadata and ciphertext hashes, never credentials, released keys or customer plaintext.

## Audit against origin/main

| Surface | Existing behavior | Gap closed |
|---|---|---|
| Region database files | AEGIS pages, sidecar removal, permanent local purge | Shared custody deletion authority reaches disconnected catalogs after reconnect; acknowledged revocation also retires native replicas and their separate page-key sidecars |
| JetStream push stream | Compressed SQL | TBP1 per-database ciphertext before durable publish; database-subject purge |
| Regional partner/system backup | Decoded transaction persisted | Reuses exact TBP1 ciphertext |
| Shape R2/CDN | F6 TBS1 under DB envelope key | Owned-object removal, durable purge outbox, failed/restarted purge retry |
| Continuous/customer backups | TBK1 per-database encryption | Keys moved outside every snapshotted destination; deletion synchronously cancels shipping/snapshot work and closes retained WAL readers |
| Managed snapshots and their continuous copies | Separate engine database IDs and envelope-key scopes | Source deletion cascades to snapshot scopes; expiry shreds before removing ownership |
| Key caches | Regional selection/unwrap caches | Warm decode uses memory with zero GETs; permanent tombstone checks on cold decode, generation fence, and synchronous eviction acknowledged by every discovered node |
| Tenant erasure | Version-2 project fences, local purge receipts | Closure acceptance waits for key-shredding certification |

## Final live proof

The proof ran after the format-23 reset and after the backup-reader correction deployed:

- Organization `01M6P8XX19296WBN1E9FF1A770`, project `01M6P8XX36FZTNV2CCK4K73F44`.
- Database `01M6PEAQ2A8JXCR0EWPWNJ5R2P`, home gru, replicas sjc and fra.
- Managed snapshot `01M6PEAV5E98VX80A0ZGBAW2D9` and shape `h1-000240d9abffea65`.
- Local-write requests from sjc and fra both returned 200 with `route=local; reason=override; commit=local` and requested home durability.

Before DELETE, SELECT returned the synthetic marker in all three regions, PITR and the ready manual backup were available, and both source and snapshot custody keys returned 200 from every region. Tigris source/snapshot objects had TBK1 headers and contained no wrapped envelope keys. Shape-key release was warmed twice in each region. The immutable CDN object returned 200/HIT twice in GRU, SJC and FRA, with the manifest ciphertext hash and `Cache-Control: public, max-age=31536000, immutable`.

DELETE completed at **18:10:41.489331Z**. The table gives first observed API/key/file failures and the later repeated CDN sample, in seconds after the 204 response. Sequential regional TLS probes make these sampling upper bounds; the service had already certified key destruction before returning 204.

| Region | SQL 404 | PITR/backups 404 | Restore 404 | Shape-key 404 | R2 source / snapshot key 404 | Files absent | CDN 404 |
|---|---:|---:|---:|---:|---:|---:|---:|
|gru|0.362|1.036|1.718|2.057|1.706 / 1.626|0.814|69.328|
|sjc|1.237|3.672|6.098|7.311|3.737 / 3.804|2.827|71.879|
|fra|1.284|3.816|6.359|7.630|3.650 / 3.562|2.841|72.153|

Each custody scope listed only its permanent `shredded` marker; individual GETs of the exact previously readable wrapped-key objects returned 404. All source/snapshot native files and sidecars were absent in every region. The final API sample repeated the 404 results, and the CDN URL returned 404/BYPASS twice from each region. Direct R2 origin HEADs also returned 404.

Public URL, without credentials:

`https://shapes-ts-terrabase-shapes-prod.terrascale.tech/v1/shapes/h1-000240d9abffea65/a22561dacb082427fc7bc61054d71ebaca1af3518a523a229fdc994092f4fe78`

The shared shape inventory contained 704 objects at 18:10:02Z. The documented erasure worker visits 16 database jobs per pass, scans at most 1024 objects or 30s per database visit, and saves its cursor; backlog and object position can require multiple visits. CDN reclamation is asynchronous. Successful DELETE synchronously destroys custody keys and revokes all discovered nodes; it does not depend on CDN purge for confidentiality. No finite provider-outage completion time is promised.

[Raw live evidence](2026-11-05-f11-live.json) contains the final baseline, immediate and final samples, all-six-node health, exact timings, custody GETs, CDN headers, failure cases and account cleanup.

## Corrections and qualification

The decode-cache fix shipped as `f7a8881d5594f7f552d3b31e403e94409df4d152`, with [green CI 42061642037](https://github.com/TerraScale/terrascale/actions/runs/42061642037). Warm decode now performs zero object-store GETs. Permanent tombstones, shared cache invalidation and a generation fence prevent stale cached decode after deletion.

The first live proof exposed orphan native `.replicas` files and their independent node-wrapped page-key sidecars. `80303f9d36c24cdea1976225bd234ecc4c40127a` added acknowledged native teardown and serialized follower admission with deletion. Its [CI 42067463433](https://github.com/TerraScale/terrascale/actions/runs/42067463433) finished green; the rollout retry finished 16:58:05Z. An initial deployment attempt had failed before replacing nodes because an orchestrator release download found no matching assets.

Two subsequent deletions returned 503 while a continuous-backup WAL reader remained open. They failed closed and durable retries completed: source/snapshot keys, regional APIs and files were subsequently unavailable in all regions. Their cached URLs later returned 404 everywhere, sampled no later than 1432.998s and 1777.379s after the failed responses. They are preserved separately and are not the successful-204 contract proof.

The retained-reader issue was reproduced on the unmodified 803 implementation by driving real continuous shipping in the existing happy-path test: expected 204, received 503 in 3.520s; the critical-failure test passed. The final correction fences/unlinks the engine copy, cancels and joins per-database shipping and snapshot work, disposes its retained reader and scratch buffers, then requires the existing engine erasure verification. This also prevents discovery from reopening a still-catalogued snapshot during retirement.

Final `ts-ship` Release qualification of the deployed correction:

| Area | Result | Fast wall time | Build time |
|---|---:|---:|---:|
|TerraBase|62/62|17.389s|141.913s|
|Auth|30/30|7.455s|60.975s|
|Native driver|4/4|7.344s|included above|

The retained-reader shred happy path took 5.856s and the critical-failure test 4.739s. Analyzer and checked-build/E2E gates passed. F11 retains exactly one happy-path and one critical-failure test. The correction adds no work to normal writes or decode and needs no further format change or reset.

## Custody outside snapshots

[Cloudflare configuration evidence](2026-11-05-f11-custody-config.json), inspected at 13:41 UTC. R2 ts-terrabase-shapes-prod v1/keys: cf lifecycle abort multipart globally after86400s; only object deletion rule is v1/shapes after691200s. Migration jobs[], Super Slurper jobs[], Sippy disabled, local uploads disabled, locks[], event notification configuration404 (11015). R2 Get/PutBucketVersioning and Get/PutBucketReplication are unsupported per https://developers.cloudflare.com/r2/api/s3/api/ . Existing runtime identity returns403 to their GET config APIs, which is not proof of disabled settings. No new bucket, credentials, permission or capacity.

Tigris pre-reset inventory: terrascale-gru-terrabase-backups-b12, HeadBucket200 snapshotEnabled=false; snapshot inventory400 InvalidArgument Wrong bucket type. The first listing page contained 500 legacy v1/keys objects, including 142 wrapped keys (80 bytes each). The reset owner subsequently reported deletion of510 exact legacy Tigris versions and24 preserved archive directories, completed the format23 reset and reseeded Demo. The live proof began only after this reset. Reset owner receives exact prefix and local preserved snapshot directories; no bucket deletion.

## Account closure

The fresh `f11-closure-cache` account had a gru database with sjc/fra replicas, a shape and a manual backup. Its DB DELETE returned204 at16:21:21.977Z. Account deletion began16:21:22.125Z, remained202 at16:21:24.314Z, and was confirmed DELETED at16:21:59.397Z: observed closure interval2.189–37.272s from initiation, upper bound37.420s after DB DELETE. The proof account `f11-after-reset` subsequently closed in4.519s on its first attempt. Private state and API-key files were removed for both. These are observed latency intervals, not a tighter guaranteed completion time.

All three reported pending accounts are closed: `r8-failover-20261105-sol-final` (owner evidence15:26:56Z), `shape-cdn-b9c6c5dd` (cleanup confirmed15:39:05Z), and `fastwrites-hlc-final-20261105` (fast-writes owner confirmation relayed by parent). The first two confirmations predate cache deployment. Their earlier pending state was delayed convergence, not permanent closure failure.

The final `f11-native-final` account closed on its first cleanup request at **18:12:50.077Z**, 2.654s after initiation. Its private account state and API-key files are absent. Earlier failed attempts used this same synthetic account and had already completed their durable cleanup. Closure latency varies with the auth/erasure workers; the earlier 2.189–37.272s observed interval and this final 2.654s completion do not establish a fixed completion SLO.

## Sealing cost

One HKDF-SHA256, random32-byte salt and AES-GCM per compressed transaction; ciphertext reused for both durable copies.78 bytes overhead. Warm key selection and decode perform no I/O. Cold decode checks custody authority, and DELETE synchronously revokes warm caches. The measured kernel excludes key selection, cold R2 I/O, compression and end-to-end acknowledgement. Delta (Intel i7-13620H, x64), Release/net11.0, ts-heavy-any,1,000 warmups then3×10,000 seals per size. Production `TransactionCrypto.Seal` is unchanged from the shipped revision. Locked restore and benchmark build passed after refreshing its pre-existing stale lock file.

| Compressed bytes | Repeat1 (us) | Repeat2 (us) | Repeat3 (us) | Allocated bytes/seal |
|---:|---:|---:|---:|---:|
|256|7.066|4.992|6.601|1056|
|4096|10.225|11.569|11.473|4896|
|65536|34.771|46.930|33.656|66297–66337|

[Raw measurements](2026-11-05-f11-seal-cost.csv). Cost HANDOFF sent to fast-writes. No local-ack policy or read-pull changes.

## Decode-cache latency

Three measured repeats of ten inserts, with one excluded warmup per repeat. Reused HTTPS connection from beta to pinned SJC; local execution preference and home durability requested. All 66 total before/after requests returned 200. Before runtime `7091b0b`; after runtime `f7a8881d5`. Other commits landed between deployments, so this is descriptive end-to-end evidence. The integration test directly establishes ten warm decodes with zero additional object-store GETs.

| Repeat | Before client median / max (ms) | After client median / max (ms) | Before / after app median (ms) |
|---:|---:|---:|---:|
|1|922.219 / 2058.800|626.638 / 818.459|516.350 / 221.510|
|2|847.907 / 993.348|622.837 / 754.500|445.730 / 215.485|
|3|941.404 / 1047.908|617.095 / 639.684|538.985 / 213.565|

[Raw latency samples](2026-11-05-f11-decode-latency.json). Medians use the middle-pair average; no percentile claim from ten samples. Ciphertext format, sealing cost and write durability policy are unchanged by the cache and teardown fixes. Fast-writes received the shipped-cache HANDOFF and receives this final report.
