# Vector index (ANN) build, near-query latency and recall

Measured: 2026-10-24T12:17:26Z. Region gru. Server: the #611 change as reviewed (merged as cfe41939c), built on main e30bc83b7. Local two-region test cluster (gru/sjc, tests/TerraBase.E2E BenchmarkClusterHost, Release) on mariogk-dev (46 CPUs, shared with other builds), client on the same host at nice 19; test-only issuer, no production credentials.

100000 documents of 768-dim embeddings; each repeat drops and rebuilds the `kind: vector` index on the loaded collection, then runs 200 different held-out query vectors once each (cosine, limit 10, no filter), sequentially. Latency is end-to-end HTTP. Build ms runs from the create request until the index lists as active. Recall@10 is against exact cosine neighbours computed by the client over the same float32 values.

| Repeat | Build ms | Queries | p50 ms | p95 ms | p99 ms | Recall@k mean | Recall@k min |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 43550 | 200 | 28.7 | 38.9 | 60.9 | 0.9965 | 0.80 |
| 2 | 101113 | 200 | 28.6 | 37.8 | 65.5 | 0.9965 | 0.80 |
| 3 | 95249 | 200 | 56.3 | 118.0 | 158.0 | 0.9965 | 0.80 |
