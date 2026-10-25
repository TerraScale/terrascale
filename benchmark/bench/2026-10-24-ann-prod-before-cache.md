# Vector index (ANN) build, near-query latency and recall

Measured: 2026-10-24T20:23:42Z. Region api. Production (TerraBase StorageFormat 18 image from batch 7, deployed 2026-10-24T19:44Z), database homed in gru on a Pro organization of a test user; requests go through the public api.terrascale.tech edge pinned to gru (X-TerraScale-Region-Pin). Client on mariogk-dev; TCP connect to the edge took about 22 ms, which every query's latency includes.

100000 documents of 768-dim embeddings; each repeat drops and rebuilds the `kind: vector` index on the loaded collection, then runs 200 different held-out query vectors once each (cosine, limit 10, no filter), sequentially. Latency is end-to-end HTTP. Build ms runs from the create request until the index lists as active. Recall@10 is against exact cosine neighbours computed by the client over the same float32 values.

| Repeat | Build ms | Queries | p50 ms | p95 ms | p99 ms | Recall@k mean | Recall@k min |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 51286 | 200 | 52.3 | 194.5 | 307.1 | 0.9965 | 0.80 |
| 2 | 81850 | 200 | 77.3 | 174.7 | 197.2 | 0.9965 | 0.80 |
| 3 | 86899 | 200 | 76.1 | 200.2 | 301.2 | 0.9965 | 0.80 |
