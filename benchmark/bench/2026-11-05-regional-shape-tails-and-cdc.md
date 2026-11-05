# Regional shape tails and bounded CDC

The live tail run used TerraBase `4b04ad56f711ab7e10735273a037f67dfa60464b`, including the F9/F5 fixes in `d9a35dc535c805c73aa7c4a06bf481f474780bcc`. A fresh Free account held a gru-home database with sjc and fra replicas. Clients ran on the corresponding replica nodes. Every live continuation and replay returned HTTP 200 from that region's node; none required a refetch. The account was deleted.

| Client | Reader | Consecutive updates | Replay median / maximum | Write start to tail median / maximum |
| --- | --- | ---: | ---: | ---: |
| sjc | Project token, filtered shape | 30 | 15.35 / 195.0 ms | 575.0 / 771.1 ms |
| sjc | End-user token, filter and column projection | 10 | 15.90 / 26.0 ms | 584.35 / 783.8 ms |
| fra | Project token, filtered shape | 30 | 13.85 / 202.3 ms | 626.9 / 1045.6 ms |
| fra | End-user token, filter and column projection | 10 | 14.70 / 18.5 ms | 722.55 / 834.4 ms |

Write-to-tail timing includes the client's cross-region write request to gru. Replay measures a subsequent read at the replica. Before these fixes, a small three-sample home-forwarded replay baseline measured 361.0 ms median from sjc and 403.1 ms from fra; those small baseline samples are descriptive, not a throughput comparison. Every end-user operation contained only the granted `value` column.

The reproducible runner is [shape-tails](../shape-tails/README.md). Raw public observations are in [f9f5-live-20261105.json](f9f5-live-20261105.json). Credentials stayed in private helper files or stdin.

## Fresh account after the production reset

The shipped runner at `60141fccf807a9846d267b5d545e086b3f83bc07` repeated the proof after the reset with a newly created Free account. All three serving nodes still ran `4b04ad56f711ab7e10735273a037f67dfa60464b`. Each region delivered 30 consecutive filtered project-token updates and ten filtered/projected end-user updates, with HTTP 200 on every continuation and replay, the matching regional serving header, and no `must_refetch`. The sjc node identified itself as `ts-sjc-1`; fra identified itself as `ts-fra-1`. The account was deleted successfully. These observations are recorded under `post_reset_tails` in the raw JSON.

| Client | Reader | Consecutive updates | Replay median / maximum | Write start to tail median / maximum |
| --- | --- | ---: | ---: | ---: |
| sjc | Project token, filtered shape | 30 | 189.6 / 272.2 ms | 769.1 / 1150.5 ms |
| sjc | End-user token, filter and column projection | 10 | 16.75 / 23.7 ms | 573.25 / 776.7 ms |
| fra | Project token, filtered shape | 30 | 13.8 / 213.1 ms | 627.7 / 1131.9 ms |
| fra | End-user token, filter and column projection | 10 | 14.55 / 25.0 ms | 628.25 / 830.0 ms |

A further fresh-account confirmation after the format-22 release notice again delivered all 40 updates locally in each region, with no refetch and successful account deletion. The inspected image label remained `4b04ad56f`, which includes the requested `7f02e9823`. In sjc, replay medians were 129.95 ms for the 30 project-token updates and 15.9 ms for the ten end-user updates; write-start-to-tail medians were 768.95 and 734.05 ms. The corresponding fra replay medians were 13.75 and 14.4 ms. Raw observations are under `format22_confirmation`.

## Retention safety

The retained critical integration case checks that cleanup cannot pass a live shape, an unstored backup segment, an R3 replica position or a registered push-consumer position. It also verifies successful catalog shipping, an active grant restored from the initial snapshot, and a later grant revocation restored through CDC. Consumer release now triggers cleanup without needing a shape sweep, always capped by the durable backup and every remaining floor.

The catalog guard fixes a schema-ownership error: system catalogs have no `_tb_shape_chunk` table. The unchanged guard and the added catalog-shipping assertions passed five full TerraBase Fast runs on `e835d3db26e3103f805274bda17f6143a65826af`: 94/94 tests in each run, with no skips. Suite times including the driver were 41.621, 42.290, 31.686, 32.941 and 33.295 seconds on a contended dev host; the temporary #1084 warning policy applied. Two later clean-rebase runs also passed, at 21.511 and 20.734 seconds including the driver, with the retention case under nine seconds.

Five further full Fast repeats on the shipped `1c15bcda38c9f9f46af05b2b70cb5adafcfd2a2e` passed on delta through `ts-heavy-any`: 96/96 TerraBase tests and 5/5 driver tests in every run, with zero failures or skips. TerraBase suite times were 19.449, 14.164, 12.615, 13.043 and 13.100 seconds. The retention case took 13.286, 8.425, 7.000, 8.492 and 7.382 seconds; the first run emitted the temporary #1084 per-test timing warning. The catalog regression assertions are on main in `d98c02048afeec1b32719308b1b7129b82b56c6e`.

## CDC load

Before consumer-triggered cleanup, one 1,024-update round remained at 2,089 retained change-id positions after backup lag reached zero. The proof database occupied 4,108,288 bytes with a 646,872-byte WAL. This failed drain exposed the ordering gap: the durable backup can acknowledge before a replica releases its pin, and an idle database with no shapes otherwise has no later cleanup trigger. That throwaway account was deleted. The raw failed-drain observations are retained in the JSON alongside the tail results.

The consumer cleanup change is on main at `1c15bcda38c9f9f46af05b2b70cb5adafcfd2a2e`. Its TerraBase and auth proof checks passed; TerraBase Fast took 20.532 seconds. Its CI runtime-payload restore failed separately: Cloudflare returned HTTP 403 for the checked R2 bundle in [run 41947282761](https://github.com/TerraScale/terrascale/actions/runs/41947282761).

The final load proof waits for a green deployment containing the consumer cleanup change, then uses another fresh throwaway account. The post-reset tail run confirmed production was still on `4b04ad56f`, before that change. No bounded-storage result is claimed yet.
