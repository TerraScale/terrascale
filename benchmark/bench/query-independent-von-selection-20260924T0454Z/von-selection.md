# Query-independent Von selection qualification

Generated: 2026-09-24T04:51:01.3195694+00:00; documents per fresh root: 2000; trials: 3; repeat samples: 30; maximum indexes: 2; physical storage cap: 8 MiB per collection and tenant; logical storage cap: 4194304 bytes per collection and tenant.
The declared field roles and both seeds are persisted in `selection-plan.json` before inference. The same document seed and post-advice predicate draw are used for all three arms in each trial. Each trial draws two held-out predicates after candidate generation, advice, and index application. No prior query observations or preferred paths enter selection.
Von runs on the configured local endpoint; only its returned model/version and token usage are reported. Token cost is unpriced because no service price is configured. A fallback remains a fallback.
Loaded TerraBase DLL SHA-256: `235905372a26957de2f8f74f197291915d1d2ec73e68adb95db16a7bdc685e3e`; benchmark DLL SHA-256: `98be40a991bc8dcd08e251b11f8102211822c0e3ffb6c5cb3c3d54f155d18ee7`.

| Field path | Predeclared operational description |
| --- | --- |
| `account.lookupId` | Stable account identifier used by operators to look up one account. |
| `payload` | Opaque export payload; it has no lookup, filtering, or ordering role. |
| `review.queue` | Review queue code used by operators to filter assigned accounts. |
| `service.region` | Service region code used by operators to filter accounts for regional operations. |

| Trial | Arm | Held-out predicate | Active paths | First µs | Repeat p50 µs | Repeat p95 µs | Expected IDs | First/repeat index served | Ingest ms | Profile ms | Advice ms | Build ms | Final file B | Advisor outcome | Model | Tokens in/out | Cost |
| ---: | --- | --- | --- | ---: | ---: | ---: | ---: | --- | ---: | ---: | ---: | ---: | ---: | --- | --- | --- | --- |
| 1 | no-index | `service.region = region-22` |  | 115798.0 | 51551.7 | 81679.4 | 40 | False/0 % | 338.1 | 0.0 | 0.0 | 0.0 | 67131991 | not-run:not-run | —/— | —/— | unpriced |
| 1 | no-index | `account.lookupId = acct-000299` |  | 15030.3 | 13263.8 | 14315.3 | 1 | False/0 % | 338.1 | 0.0 | 0.0 | 0.0 | 67131991 | not-run:not-run | —/— | —/— | unpriced |
| 1 | deterministic | `service.region = region-22` | `account.lookupId`, `payload` | 23018.6 | 13943.7 | 20937.6 | 40 | False/0 % | 126.5 | 179.6 | 1.9 | 3502.3 | 67341749 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 1 | deterministic | `account.lookupId = acct-000299` | `account.lookupId`, `payload` | 19526.2 | 20.4 | 611.2 | 1 | True/100 % | 126.5 | 179.6 | 1.9 | 3502.3 | 67341749 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 1 | von | `service.region = region-22` | `account.lookupId`, `payload` | 16997.7 | 23891.5 | 35359.5 | 40 | False/0 % | 134.1 | 30.5 | 30063.4 | 3726.9 | 67349375 | schema-statistics:von-timeout | von-1.2.0/— | —/— | unpriced |
| 1 | von | `account.lookupId = acct-000299` | `account.lookupId`, `payload` | 1188.0 | 14.3 | 42.8 | 1 | True/100 % | 134.1 | 30.5 | 30063.4 | 3726.9 | 67349375 | schema-statistics:von-timeout | von-1.2.0/— | —/— | unpriced |
| 2 | deterministic | `account.lookupId = acct-001697` | `account.lookupId`, `payload` | 1219.7 | 20.5 | 73.3 | 1 | True/100 % | 103.2 | 18.9 | 0.0 | 1624.4 | 67345685 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 2 | deterministic | `review.queue = queue-10` | `account.lookupId`, `payload` | 16523.5 | 13713.6 | 17909.2 | 50 | False/0 % | 103.2 | 18.9 | 0.0 | 1624.4 | 67345685 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 2 | von | `account.lookupId = acct-001697` | `account.lookupId`, `payload` | 866.9 | 12.9 | 64.8 | 1 | True/100 % | 108.5 | 10.3 | 30001.9 | 1417.5 | 67347973 | schema-statistics:von-timeout | von-1.2.0/— | —/— | unpriced |
| 2 | von | `review.queue = queue-10` | `account.lookupId`, `payload` | 13398.4 | 13851.0 | 15706.1 | 50 | False/0 % | 108.5 | 10.3 | 30001.9 | 1417.5 | 67347973 | schema-statistics:von-timeout | von-1.2.0/— | —/— | unpriced |
| 2 | no-index | `account.lookupId = acct-001697` |  | 12958.3 | 13748.8 | 18283.7 | 1 | False/0 % | 89.9 | 0.0 | 0.0 | 0.0 | 67131991 | not-run:not-run | —/— | —/— | unpriced |
| 2 | no-index | `review.queue = queue-10` |  | 16221.5 | 13628.1 | 15709.9 | 50 | False/0 % | 89.9 | 0.0 | 0.0 | 0.0 | 67131991 | not-run:not-run | —/— | —/— | unpriced |
| 3 | von | `review.queue = queue-08` | `account.lookupId`, `payload` | 13663.4 | 13105.6 | 14983.8 | 50 | False/0 % | 97.5 | 14.0 | 29323.6 | 1430.3 | 67368805 | schema-statistics:von-uncertain-or-abstained | von-1.2.0/von-1.2.0 | 1129/9 | unpriced |
| 3 | von | `account.lookupId = acct-001095` | `account.lookupId`, `payload` | 882.4 | 13.5 | 23.1 | 1 | True/100 % | 97.5 | 14.0 | 29323.6 | 1430.3 | 67368805 | schema-statistics:von-uncertain-or-abstained | von-1.2.0/von-1.2.0 | 1129/9 | unpriced |
| 3 | no-index | `review.queue = queue-08` |  | 13192.7 | 24568.8 | 27942.3 | 50 | False/0 % | 79.5 | 0.0 | 0.0 | 0.0 | 67131991 | not-run:not-run | —/— | —/— | unpriced |
| 3 | no-index | `account.lookupId = acct-001095` |  | 12909.9 | 12990.2 | 14451.2 | 1 | False/0 % | 79.5 | 0.0 | 0.0 | 0.0 | 67131991 | not-run:not-run | —/— | —/— | unpriced |
| 3 | deterministic | `review.queue = queue-08` | `account.lookupId`, `payload` | 13576.4 | 13043.8 | 19662.5 | 50 | False/0 % | 80.1 | 11.7 | 0.0 | 1429.6 | 67336736 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |
| 3 | deterministic | `account.lookupId = acct-001095` | `account.lookupId`, `payload` | 772.6 | 11.5 | 18.2 | 1 | True/100 % | 80.1 | 11.7 | 0.0 | 1429.6 | 67336736 | schema-statistics:query-independent-coverage-hypothesis | —/— | —/— | unpriced |

Every timed first and repeat query checks the exact document ID set. Raw repeat samples, candidate recommendations, and allowlisted Von choice/assessment evidence are in `von-selection-receipt.json`. Model confidence is not a measured query benefit. The no-index and deterministic arms receive the same TerraBase caps and independently seeded equivalent documents.
