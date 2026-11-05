# R10 live regional two-node acknowledgements — 2026-11-05

All 90 writes acknowledged locally with a healthy regional partner. Each region's holder recorded 30 successful durable-hold responses, and all 30 rows reached its remote home. Measurements ran in order: SJC, GRU, FRA, with three consecutive rounds of ten single-row ULID inserts per region.

| Region | Round | Client p50 (ms) | Client p95 (ms) | Server app p50 (ms) |
| --- | --- | --- | --- | --- |
| sjc | 1 | 19.029 | 781.587 | 16.755 |
| sjc | 2 | 17.877 | 19.285 | 14.705 |
| sjc | 3 | 18.673 | 40.116 | 16.015 |
| gru | 1 | 14.909 | 1067.364 | 12.880 |
| gru | 2 | 15.282 | 21.027 | 13.305 |
| gru | 3 | 15.491 | 58.828 | 13.565 |
| fra | 1 | 16.711 | 1293.421 | 14.235 |
| fra | 2 | 15.509 | 39.333 | 13.590 |
| fra | 3 | 15.123 | 42.123 | 13.355 |

The client ran on each region's founder over persistent HTTPS to the regional public API. OAuth exchange, database/schema creation, replica readiness and metrics collection were outside the write timer. Every measured response was HTTP 200, identified the requested region, and returned `route=local; reason=ulid_insert; commit=local`. SQL used `/sql/execute`. There was no write warm-up: the first write of each fresh database is included (SJC 781.587 ms, GRU 1067.364 ms, FRA 1293.421 ms). With ten samples, nearest-rank p95 equals the round maximum; this is a small live proof, not a throughput or tail-latency characterization. Raw client samples and Server-Timing values are in [samples.md](samples.md).

| Origin | Holder | OCI fault domains (origin / holder) | Holder POST /v1/sync/holds 204 count | Remote home | Rows at home | Database deletion |
| --- | --- | --- | --- | --- | --- | --- |
| ts-sjc-1 | ts-sjc-2 | 1 / 2 | 0 → 30 | gru | 30 | 204 |
| ts-gru-1 | ts-gru-2 | 3 / 2 | 0 → 30 | fra | 30 | 204 |
| ts-fra-1 | ts-fra-2 | 3 / 1 | 0 → 30 | gru | 30 | 204 |

Independent founder scrapes showed `terrabase_localack_partner=1` and `terrabase_localwrites_available=1` in all three regions. Each founder's ts-orch inventory showed two running tasks and its peers file listed the founder before the holder. Holder metrics were read through the existing authenticated internal listener; credentials stayed in stdin or memory. N1's OCI inventory and health proof confirmed six healthy A1 nodes in distinct fault domains within each region. No production node was killed.

Implementation shipped at `59c46e4ac1b17da58222ca18d22b44e8b162a855` (implementation `bf1e6d134`, quorum `0c9d256c6`). All three measured services reported image digest `sha256:5f85dc89d527f5a476eb25452b72a82ae8ac43f9b1e5b957ad9676c2f071b247`. N1 placement shipped at `6f5a44f918e41aacdb1633fe70606fee607cbefa`; worker-aware rollout validation at `ec3dc2d47bef8a8285e061c5b59a00e8492127f1`; its green release includes provenance correction `cacf9bc04256bef5fb745389e744a959fdc3a0a8`.

The exact implementation Fast gates passed before shipping. The post-F7 Slow proof passed the two acknowledgement cases (2.479 s), the origin-node/push-stream loss drill (12.431 s, all three acknowledged writes recovered), R7 push/recovery (2.779 s), and ShapeOverlay confirmation/reversion (2.654 s). The user-approved one-node discovery quorum remains a separate behavior; these live measurements require an actual configured, healthy second node.

All three proof databases were deleted (204). Both Free synthetic accounts were fully deleted; their private key and session-state files are absent. The account used for the measurements completed asynchronous regional closure on 2026-11-05 at 16:39 UTC.
