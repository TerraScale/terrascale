# P04 focused paired repeat — p04-repeat-20260924-a2

Status: **valid**. Acceptance qualified: **false**.

Protocol SHA-256: `138abc0c939335103311d8edf97a1a060dd68f0d728351f4d5380bd82bde0a22`. Estimator: `sum(T_i)/sum(S_i)`; 10000 whole-pair bootstrap draws per metric; seed `2026092404`.

| Pair | Order | SQLite first / p50 / p95 / p99 / p99.9 (ns) | TerraBase first / p50 / p95 / p99 / p99.9 (ns) | Status | Receipts |
| --- | --- | --- | --- | --- | --- |
| 01 | SQLite→TerraBase | 118508 / 4989 / 15986 / 58685 / 401718 | 1055424 / 852871 / 1459874 / 1624653 / 2856647 | complete | [p04a201s](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a201s-receipt.json>); [p04a201t](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a201t-receipt.json>) |
| 02 | TerraBase→SQLite | 152183 / 5675 / 16540 / 63600 / 422670 | 1799938 / 992925 / 2211226 / 14601746 / 25336601 | complete | [p04a202s](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a202s-receipt.json>); [p04a202t](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a202t-receipt.json>) |
| 03 | SQLite→TerraBase | 359592 / 5504 / 15081 / 58433 / 220756 | 878846 / 829647 / 1428820 / 1577905 / 6325175 | complete | [p04a203s](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a203s-receipt.json>); [p04a203t](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a203t-receipt.json>) |
| 04 | TerraBase→SQLite | 165991 / 5766 / 16193 / 65028 / 456204 | 1660748 / 779787 / 1415332 / 1652793 / 2773043 | complete | [p04a204s](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a204s-receipt.json>); [p04a204t](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a204t-receipt.json>) |
| 05 | SQLite→TerraBase | 81926 / 8209 / 17039 / 90091 / 521046 | 1455937 / 831787 / 1456694 / 1613480 / 5460350 | complete | [p04a205s](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a205s-receipt.json>); [p04a205t](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a205t-receipt.json>) |
| 06 | TerraBase→SQLite | 125975 / 5050 / 16516 / 55711 / 140151 | 1933915 / 753557 / 1585134 / 2573277 / 3295233 | complete | [p04a206s](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a206s-receipt.json>); [p04a206t](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a206t-receipt.json>) |
| 07 | SQLite→TerraBase | 166100 / 3595 / 15276 / 30387 / 105509 | 1488750 / 813756 / 1394180 / 1623440 / 2764164 | complete | [p04a207s](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a207s-receipt.json>); [p04a207t](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a207t-receipt.json>) |
| 08 | TerraBase→SQLite | 114121 / 12366 / 17268 / 204597 / 269956 | 1167514 / 820827 / 1387160 / 1828955 / 3491381 | complete | [p04a208s](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a208s-receipt.json>); [p04a208t](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a208t-receipt.json>) |

| Metric | Ratio T/S | 95% CI | Provisional classification | Resample-index SHA-256 |
| --- | ---: | --- | --- | --- |
| first_after_fixture_ns | 8.907745 | [5.558630, 12.939512] | provisional_loss | `3f184591749d043f0306322f43908c92af1b1757a7645549de1abca2e0de7a41` |
| warm_p50_ns | 130.491399 | [99.249485, 170.803309] | provisional_loss | `5c060fbd9c9fb94de8e90a6de79a936511bf84bd68642ef0b0f98c6fc99fb3c7` |
| warm_p95_ns | 94.984719 | [86.698367, 107.195314] | provisional_loss | `b18016dcd961649b433a4325765aed7f61d240dc1f07b2ef3ab73187fa7d3863` |
| warm_p99_ns | 43.247989 | [16.098243, 105.178323] | provisional_loss | `02b42b13028c94157166d37cc7801cd194e3f4ec1bc4cab47cf3ce13cb541fc3` |
| warm_p999_ns | 20.607718 | [9.706295, 38.546379] | provisional_loss | `3034cbaad40b5efbec1f9b50f674a36fbe2ca412695e9c68bc96b7f61ca48a9f` |

D01 is a separate diagnostic pair and contributes no latency samples to the intervals.

D01: **complete**; receipts: [p04a2d01s](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a2d01s-receipt.json>), [p04a2d01t](</root/fc-bench-data/p04-analyzer-input/p04-repeat-20260924-a2/p04a2d01t-receipt.json>).

## Failures and qualification limits

- host cache reset, exclusive host CPU, upstream image provenance and complete resource gates require independent qualification
- provisional focused P04 timing cannot close the full P04 matrix row
- the hash-pinned build artifact map is an operator assertion, not a cryptographic proof that the measured ELF was compiled from the listed inventory

Machine-readable hashes, individual paired values, analyzer identity, and input paths are in the companion JSON report.
