# Query-independent index inspection and feed trace

## Run

One completed comparison used 2,000 seed documents, 100 maintenance updates, 10 repeat samples, a two-index limit, and an 8 MiB per-collection/per-tenant automatic-index budget. Both TerraBase indexed lanes returned the expected 42 status matches through an equality seek without snapshot fallback. The Von attempt selected deterministic fallback; this run does not show a Von benefit.

## First status query phases

| Lane | First query | Long index gate wait | Concurrent feed work | Inspection body |
| --- | ---: | ---: | --- | ---: |
| Deterministic schema statistics | 912.836 ms | 878.351 ms (`index.gate_wait`) | `feed.frame_apply`: 879.982 ms; transaction staging: 873.353 ms | 0.173 ms total |
| Deterministic fallback after Von attempt | 784.980 ms | 782.728 ms (`index.inspect.gate_wait`) | `feed.frame_apply`: 782.809 ms; transaction staging: 780.523 ms | 0.019 ms |

The long query time coincided with feed frame application and transaction staging. Physical inspection body time was negligible in both lanes, so inspection computation itself does not explain the delay. Compaction took 0.002–0.011 ms and commits took under 1 ms. The spans establish temporal overlap; they do not by themselves prove the staging work caused all gate wait.

During the status probes the source commit remained at 22. Each index store watermark moved from 21 to 22 while lifecycle applied and target positions remained at 21.

## Provenance

- Output directory: this directory; detailed run results are in [query-independent-indexing.md](query-independent-indexing.md) and [query-independent-indexing-receipt.json](query-independent-indexing-receipt.json).
- Workload receipt: [run-receipt.json](run-receipt.json), completed successfully on 2026-09-24.
- TerraBase source commit: `47b235dcc78240a9eb5330e454a7bf3273ffcba9`.
- Captured working-tree digest: `cef6f63e06e4dd3a44961464c12a6fd2c2a252d55dac0c85fb5563eb4a2d0b3d`.
- Release runner SHA-256: `6d05f0651c5f82195b2e13ac64a0cd8d856db02715b7a259502a5ba2ddbff39a`.
- Release TerraBase SHA-256: `159a1c54279a05003501c69ed2edd89e71a60a1f04b6d5430c8caae76cffc2c8`.
- Release ZoneTree SHA-256: `67c60a6b72ccd274b136ea3bd16c93badad73f0c47acbf6cb83d38b25b3a7844`.
- Query receipt SHA-256: `38755ede5aa14a542f2cc97523df431620c3875f8c1f1138c73e735bf391de05`.
- Run receipt's recorded digest: `14d48f98e368a532ad8d5ab974af91b34b8691a343362bbbc66aee96cd9d6480`.
