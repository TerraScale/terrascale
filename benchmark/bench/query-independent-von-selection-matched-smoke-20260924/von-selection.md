# Query-independent Von selection qualification

Generated: 2026-09-24T07:08:00.3664458+00:00; documents per fresh root: 100; trials: 3; repeat samples: 10; post-build updates: 100; maximum indexes: 2; physical storage cap: 8 MiB per collection and tenant; logical storage cap: 4194304 bytes per collection and tenant.
The declared field roles and both seeds are persisted in `selection-plan.json` before inference. The same document seed and post-advice predicate draw are used for all three arms in each trial. Each trial draws two held-out predicates after candidate generation, advice, and index application. No prior query observations or preferred paths enter selection.
Von runs on the configured local endpoint; only its returned model/version and token usage are reported. Token cost is unpriced because no service price is configured. A fallback remains a fallback.
Accepted Von-advice trials: 0/3. Von attempts that selected schema-statistics advice are deterministic fallback lanes.
Loaded TerraBase DLL SHA-256: `4d8bb0dadf717c16d1e589f81012ef9b2c1ac57cd131071964efb8365bd98e99`; benchmark DLL SHA-256: `a97350dd0e5ba43661b7046cd04c5dedda49bcabe65e1b40c58e1ec3aebb5c42`.

| Field path | Predeclared operational description |
| --- | --- |
| `account.lookupId` | Stable account identifier used by operators to look up one account. |
| `payload` | Opaque export payload; it has no lookup, filtering, or ordering role. |
| `review.queue` | Review queue code used by operators to filter assigned accounts. |
| `service.region` | Service region code used by operators to filter accounts for regional operations. |

| Trial | Arm | Held-out predicate | Active paths | First µs | Repeat p50 µs | Repeat p95 µs | Expected IDs | First/repeat index served | Ingest ms | Profile ms | Advice ms | Build ms | Maintenance ms | Maintenance storage Δ B | Before-index B | After-maintenance B | Final file B | Index storage Δ B | Advisor outcome | Model | Tokens in/out | Cost |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- | --- | --- |
| 1 | no-index | `service.region = region-09` |  | 92377.8 | 3489.8 | 8905.5 | 2 | False/0 % | 142.6 | 0.0 | 0.0 | 0.0 | 28.6 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 1 | no-index | `account.lookupId = acct-000084` |  | 3455.1 | 2100.8 | 2933.1 | 1 | False/0 % | 142.6 | 0.0 | 0.0 | 0.0 | 28.6 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 1 | deterministic | `service.region = region-09` | `account.lookupId`, `payload` | 60862.0 | 2660.3 | 1101464.5 | 2 | False/0 % | 17.3 | 166.5 | 2.0 | 1485.0 | 19.0 | 0 | 67132425 | 67243437 | 67614861 | 482436 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 1 | deterministic | `account.lookupId = acct-000084` | `account.lookupId`, `payload` | 20263.0 | 48.9 | 2237.2 | 1 | True/100 % | 17.3 | 166.5 | 2.0 | 1485.0 | 19.0 | 0 | 67132425 | 67243437 | 67614861 | 482436 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 1 | von | `service.region = region-09` | `account.lookupId`, `payload` | 1123929.5 | 1394.2 | 2274.7 | 2 | False/0 % | 18.7 | 16.9 | 30109.8 | 1215.5 | 18.9 | 0 | 67132425 | 67250220 | 67621644 | 489219 | schema-statistics:von-timeout | von-1.2.0/— | —/— | unpriced |
| 1 | von | `account.lookupId = acct-000084` | `account.lookupId`, `payload` | 1216.1 | 28.8 | 86.9 | 1 | True/100 % | 18.7 | 16.9 | 30109.8 | 1215.5 | 18.9 | 0 | 67132425 | 67250220 | 67621644 | 489219 | schema-statistics:von-timeout | von-1.2.0/— | —/— | unpriced |
| 2 | deterministic | `review.queue = queue-09` | `account.lookupId`, `payload` | 1164148.5 | 1207.7 | 1962.8 | 3 | False/0 % | 10.4 | 6.5 | 0.0 | 693.1 | 8.4 | 0 | 67132425 | 67243484 | 67614908 | 482483 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 2 | deterministic | `account.lookupId = acct-000054` | `account.lookupId`, `payload` | 936.8 | 21.9 | 69.0 | 1 | True/100 % | 10.4 | 6.5 | 0.0 | 693.1 | 8.4 | 0 | 67132425 | 67243484 | 67614908 | 482483 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 2 | von | `review.queue = queue-09` | `account.lookupId`, `payload` | 945302.9 | 855.7 | 1016.2 | 3 | False/0 % | 11.2 | 4.3 | 22380.7 | 668.3 | 6.7 | 0 | 67132425 | 67270474 | 67641898 | 509473 | schema-statistics:von-uncertain-or-abstained | von-1.2.0/von-1.2.0 | 1129/9 | unpriced |
| 2 | von | `account.lookupId = acct-000054` | `account.lookupId`, `payload` | 997.0 | 25.3 | 84.6 | 1 | True/100 % | 11.2 | 4.3 | 22380.7 | 668.3 | 6.7 | 0 | 67132425 | 67270474 | 67641898 | 509473 | schema-statistics:von-uncertain-or-abstained | von-1.2.0/von-1.2.0 | 1129/9 | unpriced |
| 2 | no-index | `review.queue = queue-09` |  | 1818.2 | 1031.5 | 1082.8 | 3 | False/0 % | 9.1 | 0.0 | 0.0 | 0.0 | 6.0 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 2 | no-index | `account.lookupId = acct-000054` |  | 1194.6 | 719.1 | 870.9 | 1 | False/0 % | 9.1 | 0.0 | 0.0 | 0.0 | 6.0 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 3 | von | `service.region = region-49` | `account.lookupId`, `payload` | 1219779.9 | 717.9 | 813.4 | 2 | False/0 % | 8.5 | 5.6 | 29541.9 | 1424.9 | 11.6 | 0 | 67132425 | 67270163 | 67641587 | 509162 | schema-statistics:von-uncertain-or-abstained | von-1.2.0/von-1.2.0 | 1129/9 | unpriced |
| 3 | von | `review.queue = queue-19` | `account.lookupId`, `payload` | 1241.9 | 704.5 | 733.8 | 2 | False/0 % | 8.5 | 5.6 | 29541.9 | 1424.9 | 11.6 | 0 | 67132425 | 67270163 | 67641587 | 509162 | schema-statistics:von-uncertain-or-abstained | von-1.2.0/von-1.2.0 | 1129/9 | unpriced |
| 3 | no-index | `service.region = region-49` |  | 2484.2 | 1681.3 | 5601.2 | 2 | False/0 % | 12.3 | 0.0 | 0.0 | 0.0 | 9.7 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 3 | no-index | `review.queue = queue-19` |  | 1902.9 | 1207.3 | 1350.0 | 2 | False/0 % | 12.3 | 0.0 | 0.0 | 0.0 | 9.7 | 0 | 67132425 | 67132425 | 67132425 | 0 | not-run:not-run | —/— | —/— | unpriced |
| 3 | deterministic | `service.region = region-49` | `account.lookupId`, `payload` | 966980.7 | 705.5 | 2981.0 | 2 | False/0 % | 7.9 | 5.9 | 0.0 | 655.5 | 6.3 | 0 | 67132425 | 67243028 | 67614452 | 482027 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 3 | deterministic | `review.queue = queue-19` | `account.lookupId`, `payload` | 1251.7 | 686.0 | 2525.1 | 2 | False/0 % | 7.9 | 5.9 | 0.0 | 655.5 | 6.3 | 0 | 67132425 | 67243028 | 67614452 | 482027 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |

## Regressions versus same-trial no-index baseline

Positive latency or maintenance percentages mean slower than the no-index arm. Storage is the final sum-of-file-lengths difference in bytes. Each trial compares the same hidden predicates and document seed.

| Trial | Arm | Held-out predicate | First query Δ | Repeat p50 Δ | Maintenance Δ | Final storage Δ B |
| ---: | --- | --- | ---: | ---: | ---: | ---: |
| 1 | no-index | `service.region = region-09` | 0.0% | 0.0% | 0.0% | 0 |
| 1 | no-index | `account.lookupId = acct-000084` | 0.0% | 0.0% | 0.0% | 0 |
| 1 | deterministic | `service.region = region-09` | -34.1% | -23.8% | -33.6% | +482,436 |
| 1 | deterministic | `account.lookupId = acct-000084` | +486.5% | -97.7% | -33.6% | +482,436 |
| 1 | von | `service.region = region-09` | +1116.7% | -60.0% | -34.1% | +489,219 |
| 1 | von | `account.lookupId = acct-000084` | -64.8% | -98.6% | -34.1% | +489,219 |
| 2 | deterministic | `review.queue = queue-09` | +63927.5% | +17.1% | +38.3% | +482,483 |
| 2 | deterministic | `account.lookupId = acct-000054` | -21.6% | -97.0% | +38.3% | +482,483 |
| 2 | von | `review.queue = queue-09` | +51891.1% | -17.0% | +10.2% | +509,473 |
| 2 | von | `account.lookupId = acct-000054` | -16.5% | -96.5% | +10.2% | +509,473 |
| 2 | no-index | `review.queue = queue-09` | 0.0% | 0.0% | 0.0% | 0 |
| 2 | no-index | `account.lookupId = acct-000054` | 0.0% | 0.0% | 0.0% | 0 |
| 3 | von | `service.region = region-49` | +49001.5% | -57.3% | +20.0% | +509,162 |
| 3 | von | `review.queue = queue-19` | -34.7% | -41.7% | +20.0% | +509,162 |
| 3 | no-index | `service.region = region-49` | 0.0% | 0.0% | 0.0% | 0 |
| 3 | no-index | `review.queue = queue-19` | 0.0% | 0.0% | 0.0% | 0 |
| 3 | deterministic | `service.region = region-49` | +38825.2% | -58.0% | -34.8% | +482,027 |
| 3 | deterministic | `review.queue = queue-19` | -34.2% | -43.2% | -34.8% | +482,027 |

Every timed first and repeat query checks the exact document ID set. Raw repeat samples, candidate recommendations, and allowlisted Von choice/assessment evidence are in `von-selection-receipt.json`. Model confidence is not a measured query benefit. All three arms receive the same TerraBase caps and independently seeded equivalent documents. The same 100 existing documents receive one payload update after index build in every arm, before any evaluation query is constructed. Maintenance time and file-size change measure this one batch, not sustained write amplification. Index storage delta is final logical file bytes minus bytes before index build and the maintenance file-size change; it includes any later query-related file growth and is not physical disk-block usage.
