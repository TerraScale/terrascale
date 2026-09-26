# Query-independent Von selection qualification

Generated: 2026-09-24T07:21:51.2191072+00:00; documents per fresh root: 2000; trials: 3; repeat samples: 30; post-build updates: 100; maximum indexes: 2; physical storage cap: 8 MiB per collection and tenant; logical storage cap: 4194304 bytes per collection and tenant.
The declared field roles and both seeds are persisted in `selection-plan.json` before inference. The same document seed and post-advice predicate draw are used for all three arms in each trial. Each trial draws two held-out predicates after candidate generation, advice, and index application. No prior query observations or preferred paths enter selection.
Von runs on the configured local endpoint; only its returned model/version and token usage are reported. Token cost is unpriced because no service price is configured. A fallback remains a fallback.
Accepted Von-advice trials: 0/3. Von attempts that selected schema-statistics advice are deterministic fallback lanes.
Loaded TerraBase DLL SHA-256: `159a1c54279a05003501c69ed2edd89e71a60a1f04b6d5430c8caae76cffc2c8`; benchmark DLL SHA-256: `6d05f0651c5f82195b2e13ac64a0cd8d856db02715b7a259502a5ba2ddbff39a`.

| Field path | Predeclared operational description |
| --- | --- |
| `account.lookupId` | Stable account identifier used by operators to look up one account. |
| `payload` | Opaque export payload; it has no lookup, filtering, or ordering role. |
| `review.queue` | Review queue code used by operators to filter assigned accounts. |
| `service.region` | Service region code used by operators to filter accounts for regional operations. |

| Trial | Arm | Held-out predicate | Active paths | First µs | Repeat p50 µs | Repeat p95 µs | Expected IDs | First/repeat index served | Ingest ms | Profile ms | Advice ms | Build ms | Maintenance ms | Maintenance storage Δ B | Before-index B | After-maintenance B | Final file B | Index storage Δ B | Advisor outcome | Model | Tokens in/out | Cost |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | --- | --- |
| 1 | no-index | `service.region = region-45` |  | 151655.2 | 62069.2 | 76475.3 | 40 | False/0 % | 563.2 | 0.0 | 0.0 | 0.0 | 29.0 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 1 | no-index | `review.queue = queue-35` |  | 25967.6 | 20935.4 | 28153.3 | 50 | False/0 % | 563.2 | 0.0 | 0.0 | 0.0 | 29.0 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 1 | deterministic | `service.region = region-45` | `account.lookupId`, `payload` | 57857.0 | 25190.2 | 148672.3 | 40 | False/0 % | 264.7 | 290.3 | 3.0 | 3724.1 | 10.2 | 0 | 67132425 | 67341989 | 67713425 | 581000 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 1 | deterministic | `review.queue = queue-35` | `account.lookupId`, `payload` | 34044.3 | 21197.5 | 69527.2 | 50 | False/0 % | 264.7 | 290.3 | 3.0 | 3724.1 | 10.2 | 0 | 67132425 | 67341989 | 67713425 | 581000 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 1 | von | `service.region = region-45` | `account.lookupId`, `payload` | 987299.0 | 14110.2 | 20722.9 | 40 | False/0 % | 122.2 | 28.5 | 28771.3 | 2747.5 | 9.5 | 0 | 67132425 | 67370973 | 67742409 | 609984 | schema-statistics:von-uncertain-or-abstained | von-1.2.0/von-1.2.0 | 1129/9 | unpriced |
| 1 | von | `review.queue = queue-35` | `account.lookupId`, `payload` | 13486.1 | 22018.1 | 50257.4 | 50 | False/0 % | 122.2 | 28.5 | 28771.3 | 2747.5 | 9.5 | 0 | 67132425 | 67370973 | 67742409 | 609984 | schema-statistics:von-uncertain-or-abstained | von-1.2.0/von-1.2.0 | 1129/9 | unpriced |
| 2 | deterministic | `account.lookupId = acct-000429` | `account.lookupId`, `payload` | 1499598.9 | 15.1 | 382.5 | 1 | True/100 % | 141.4 | 16.4 | 0.0 | 2316.7 | 6.4 | 0 | 67132425 | 67344550 | 67715986 | 583561 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 2 | deterministic | `service.region = region-43` | `account.lookupId`, `payload` | 23976.9 | 23764.7 | 40773.9 | 40 | False/0 % | 141.4 | 16.4 | 0.0 | 2316.7 | 6.4 | 0 | 67132425 | 67344550 | 67715986 | 583561 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 2 | von | `account.lookupId = acct-000429` | `account.lookupId`, `payload` | 1590772.9 | 13.3 | 51.6 | 1 | True/100 % | 119.1 | 13.9 | 30004.9 | 1631.0 | 9.5 | 0 | 67132425 | 67348139 | 67719575 | 587150 | schema-statistics:von-timeout | von-1.2.0/— | —/— | unpriced |
| 2 | von | `service.region = region-43` | `account.lookupId`, `payload` | 32109.3 | 23047.0 | 64996.4 | 40 | False/0 % | 119.1 | 13.9 | 30004.9 | 1631.0 | 9.5 | 0 | 67132425 | 67348139 | 67719575 | 587150 | schema-statistics:von-timeout | von-1.2.0/— | —/— | unpriced |
| 2 | no-index | `account.lookupId = acct-000429` |  | 27487.3 | 20243.4 | 26316.8 | 1 | False/0 % | 135.0 | 0.0 | 0.0 | 0.0 | 6.5 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 2 | no-index | `service.region = region-43` |  | 15525.4 | 13591.6 | 18693.6 | 40 | False/0 % | 135.0 | 0.0 | 0.0 | 0.0 | 6.5 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 3 | von | `review.queue = queue-31` | `account.lookupId`, `payload` | 1077805.4 | 13648.2 | 26053.4 | 50 | False/0 % | 89.7 | 16.4 | 30004.3 | 1546.5 | 8.9 | 0 | 67132425 | 67349026 | 67720462 | 588037 | schema-statistics:von-timeout | von-1.2.0/— | —/— | unpriced |
| 3 | von | `account.lookupId = acct-001827` | `account.lookupId`, `payload` | 873.4 | 13.0 | 53.6 | 1 | True/100 % | 89.7 | 16.4 | 30004.3 | 1546.5 | 8.9 | 0 | 67132425 | 67349026 | 67720462 | 588037 | schema-statistics:von-timeout | von-1.2.0/— | —/— | unpriced |
| 3 | no-index | `review.queue = queue-31` |  | 25402.6 | 22336.2 | 31812.9 | 50 | False/0 % | 216.0 | 0.0 | 0.0 | 0.0 | 6.8 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 3 | no-index | `account.lookupId = acct-001827` |  | 23120.3 | 15273.7 | 25964.0 | 1 | False/0 % | 216.0 | 0.0 | 0.0 | 0.0 | 6.8 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 3 | deterministic | `review.queue = queue-31` | `account.lookupId`, `payload` | 1737810.1 | 23104.3 | 78194.2 | 50 | False/0 % | 83.7 | 10.8 | 0.0 | 1465.8 | 6.8 | 0 | 67132425 | 67343299 | 67714735 | 582310 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 3 | deterministic | `account.lookupId = acct-001827` | `account.lookupId`, `payload` | 1339.4 | 18.8 | 102.6 | 1 | True/100 % | 83.7 | 10.8 | 0.0 | 1465.8 | 6.8 | 0 | 67132425 | 67343299 | 67714735 | 582310 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |

## Regressions versus same-trial no-index baseline

Positive latency or maintenance percentages mean slower than the no-index arm. Storage is the final sum-of-file-lengths difference in bytes. Each trial compares the same hidden predicates and document seed.

| Trial | Arm | Held-out predicate | First query Δ | Repeat p50 Δ | Maintenance Δ | Final storage Δ B |
| ---: | --- | --- | ---: | ---: | ---: | ---: |
| 1 | no-index | `service.region = region-45` | 0.0% | 0.0% | 0.0% | 0 |
| 1 | no-index | `review.queue = queue-35` | 0.0% | 0.0% | 0.0% | 0 |
| 1 | deterministic | `service.region = region-45` | -61.8% | -59.4% | -65.0% | +581,000 |
| 1 | deterministic | `review.queue = queue-35` | +31.1% | +1.3% | -65.0% | +581,000 |
| 1 | von | `service.region = region-45` | +551.0% | -77.3% | -67.3% | +609,984 |
| 1 | von | `review.queue = queue-35` | -48.1% | +5.2% | -67.3% | +609,984 |
| 2 | deterministic | `account.lookupId = acct-000429` | +5355.6% | -99.9% | -1.4% | +583,561 |
| 2 | deterministic | `service.region = region-43` | +54.4% | +74.8% | -1.4% | +583,561 |
| 2 | von | `account.lookupId = acct-000429` | +5687.3% | -99.9% | +46.8% | +587,150 |
| 2 | von | `service.region = region-43` | +106.8% | +69.6% | +46.8% | +587,150 |
| 2 | no-index | `account.lookupId = acct-000429` | 0.0% | 0.0% | 0.0% | 0 |
| 2 | no-index | `service.region = region-43` | 0.0% | 0.0% | 0.0% | 0 |
| 3 | von | `review.queue = queue-31` | +4142.9% | -38.9% | +31.1% | +588,037 |
| 3 | von | `account.lookupId = acct-001827` | -96.2% | -99.9% | +31.1% | +588,037 |
| 3 | no-index | `review.queue = queue-31` | 0.0% | 0.0% | 0.0% | 0 |
| 3 | no-index | `account.lookupId = acct-001827` | 0.0% | 0.0% | 0.0% | 0 |
| 3 | deterministic | `review.queue = queue-31` | +6741.1% | +3.4% | +1.3% | +582,310 |
| 3 | deterministic | `account.lookupId = acct-001827` | -94.2% | -99.9% | +1.3% | +582,310 |

Every timed first and repeat query checks the exact document ID set. Raw repeat samples, candidate recommendations, and allowlisted Von choice/assessment evidence are in `von-selection-receipt.json`. Model confidence is not a measured query benefit. All three arms receive the same TerraBase caps and independently seeded equivalent documents. The same 100 existing documents receive one payload update after index build in every arm, before any evaluation query is constructed. Maintenance time and file-size change measure this one batch, not sustained write amplification. Index storage delta is final logical file bytes minus bytes before index build and the maintenance file-size change; it includes any later query-related file growth and is not physical disk-block usage.
