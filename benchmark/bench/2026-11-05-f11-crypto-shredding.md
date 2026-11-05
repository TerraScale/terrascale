# F11 crypto-shredding evidence — 2026-11-05

Scope: PIVOT Milestone B F11 / D19. Audit and implementation in this worktree; live proof only after the format-23 owner resets production.

## Audit against origin/main

| Surface | Existing behavior | Gap closed |
|---|---|---|
| Region database files | AEGIS pages, sidecar removal, permanent local purge | Shared custody deletion authority reaches disconnected catalogs after reconnect |
| JetStream push stream | Compressed SQL | TBP1 per-database ciphertext before durable publish; database-subject purge |
| Regional partner/system backup | Decoded transaction persisted | Reuses exact TBP1 ciphertext |
| Shape R2/CDN | F6 TBS1 under DB envelope key | Owned-object removal, durable purge outbox, failed/restarted purge retry |
| Continuous/customer backups | TBK1 per-database encryption | Keys moved outside every snapshotted destination |
| Key caches | Regional selection/unwrap caches | Historical decrypt consults shared permanent authority |
| Tenant erasure | Version-2 project fences, local purge receipts | Closure acceptance waits for key-shredding certification |

## Gates

TerraBase Fast: 62/62 passed, 19.600s; maximum7.715s. Analyzer/E2E checks passed. Shipped via `ts-ship` at `27431747eca955a9efa2a02fa860074703e61709`; final qualification TerraBase Fast12.627s (62 tests), Auth Fast7.928s (30 tests).

## Custody verification

R2 ts-terrabase-shapes-prod v1/keys: cf lifecycle abort multipart globally after86400s; only object deletion rule is v1/shapes after691200s. Migration jobs[], Super Slurper jobs[], Sippy disabled, local uploads disabled, locks[], event notification configuration404 (11015). R2 Get/PutBucketVersioning and Get/PutBucketReplication are unsupported per https://developers.cloudflare.com/r2/api/s3/api/ . Existing runtime identity returns403 to their GET config APIs, which is not proof of disabled settings. No new bucket, credentials, permission or capacity.

Tigris pre-reset inventory: terrascale-gru-terrabase-backups-b12, HeadBucket200 snapshotEnabled=false; snapshot inventory400 InvalidArgument Wrong bucket type. 500 legacy v1/keys objects,142 wrapped keys. Reset owner receives exact prefix and local preserved snapshot directories; no bucket deletion.

## Sealing cost

One HKDF-SHA256, random32-byte salt and AES-GCM per compressed transaction; ciphertext reused for both durable copies.78 bytes overhead. Warm key selection performs no I/O. Separate historical decrypt checks custody authority. The measured kernel excludes key selection, cold R2 I/O, compression and end-to-end acknowledgement. Delta (Intel i7-13620H, x64), Release/net11.0, ts-heavy-any,1,000 warmups then3×10,000 seals per size. Production `TransactionCrypto.Seal` is unchanged from the shipped revision. Locked restore and benchmark build passed after refreshing its pre-existing stale lock file.

| Compressed bytes | Repeat1 (us) | Repeat2 (us) | Repeat3 (us) | Allocated bytes/seal |
|---:|---:|---:|---:|---:|
|256|7.066|4.992|6.601|1056|
|4096|10.225|11.569|11.473|4896|
|65536|34.771|46.930|33.656|66297–66337|

[Raw measurements](2026-11-05-f11-seal-cost.csv). Cost HANDOFF sent to fast-writes. No local-ack policy or read-pull changes.

## Live proof

To be filled from private network-only harness results after format23 rollout. Must include positive preconditions, DB placement, regional node/health, snapshot/PITR evidence, prefilled CDN colos, DELETE204 duration, synchronous key GET404, post-delete reads/backup restore/shape keys in all regions, CDN404s and account cleanup. Do not claim success before these exist.
