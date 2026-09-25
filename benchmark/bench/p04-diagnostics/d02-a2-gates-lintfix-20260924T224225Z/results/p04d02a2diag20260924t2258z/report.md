# P04 D02 instrumented TerraBase scan diagnostic

Status: **diagnostic_complete**. Observational cost split only; no performance verdict, ratio, or CI.

Parent ELF `93e2099f1797033aab0263732bacb551209dad064ccc5847dcd66b3a17cf5ed2` and inventory `b5f251d333300086f15bba8df583ad6e267c7012f8975d5f892c459aff07969e` are source parents, not this binary's identity.

Derivative ELF `08876c79991489c3727afb12e07802927d839f0dde341160a2c1ce16143067d4`; inventory `deeffbbbc7b1559de4e47c286a904bee64967f20a0d012f66bd8c3da2b9303f7`; lock `3c1a4f2aabd538ad3c6e106dcf1cb6dcec62d302b33d4b4f1666be3868a2a723`; source delta `88198cfd293d895a49d6830f9b5c83eb781e2e997716984adc95c20fb5b5a5c7`.

Fixture: 1024 rows, body 1, seed 1, all rows deleted, one checkpointed run. SHA-256 `1d5c1a3d24146cea8f09abafd72ce11bbedbac9722c3fb40e3cc9ae46f0504d5`.

Empty `Instant` bracket calibration (64 fixed samples, not subtracted): min 30 / median 40 / max 235 ns.

One first-after-fixture scan and 32 ordered warm scans were requested; 32 warm scans completed.

| Phase (wall ns unless marked) | Warm n | Min | Median | Max |
| --- | ---: | ---: | ---: | ---: |
| operation inclusive | 32 | 748345 | 1278686 | 1423602 |
| Storage::scan inclusive | 32 | 747513 | 1277264 | 1422173 |
| prelude admission/allocation | 32 | 15272 | 22846 | 102191 |
| cursor open/directory | 32 | 20591 | 34542 | 40075 |
| initial advance | 32 | 83356 | 154449 | 235157 |
| merge and later advances | 32 | 552824 | 1015590 | 1174594 |
| block load (nested, non-additive) | 32 | 79731 | 146864 | 229197 |
| whole-operation thread CPU | 32 | 750621 | 1263126 | 1413960 |

The first and all 32 ordered warm slots, full work-counter deltas, checksum, and failures are retained in `/root/fc-harness-output/p04d02a2diag20260924t2258z-samples.jsonl` and `/root/fc-harness-output/p04d02a2diag20260924t2258z-receipt.json`. Unrun or missing counters remain null.

| Scan | Phase | Result | Checksum | Wall ns | Thread CPU ns | Run block bytes | Records | Error |
| ---: | --- | --- | ---: | ---: | ---: | ---: | ---: | --- |
| 0 | first_after_fixture | empty | 0 | 1321557 | 1326307 | 60436 | 2048 |  |
| 1 | warm | empty | 0 | 1373025 | 1376152 | 60436 | 2048 |  |
| 2 | warm | empty | 0 | 1253555 | 1256834 | 60436 | 2048 |  |
| 3 | warm | empty | 0 | 1196960 | 1201477 | 60436 | 2048 |  |
| 4 | warm | empty | 0 | 1296107 | 1301969 | 60436 | 2048 |  |
| 5 | warm | empty | 0 | 1267294 | 1318641 | 60436 | 2048 |  |
| 6 | warm | empty | 0 | 1308415 | 1311293 | 60436 | 2048 |  |
| 7 | warm | empty | 0 | 1278686 | 1281264 | 60436 | 2048 |  |
| 8 | warm | empty | 0 | 1198617 | 1201798 | 60436 | 2048 |  |
| 9 | warm | empty | 0 | 1294951 | 1297550 | 60436 | 2048 |  |
| 10 | warm | empty | 0 | 1340356 | 1342983 | 60436 | 2048 |  |
| 11 | warm | empty | 0 | 1259919 | 1263126 | 60436 | 2048 |  |
| 12 | warm | empty | 0 | 1341335 | 1336147 | 60436 | 2048 |  |
| 13 | warm | empty | 0 | 1331825 | 1291014 | 60436 | 2048 |  |
| 14 | warm | empty | 0 | 1292121 | 1294882 | 60436 | 2048 |  |
| 15 | warm | empty | 0 | 1423602 | 1413960 | 60436 | 2048 |  |
| 16 | warm | empty | 0 | 1289574 | 1292015 | 60436 | 2048 |  |
| 17 | warm | empty | 0 | 1303118 | 1306301 | 60436 | 2048 |  |
| 18 | warm | empty | 0 | 1337239 | 1326497 | 60436 | 2048 |  |
| 19 | warm | empty | 0 | 1040162 | 1043504 | 60436 | 2048 |  |
| 20 | warm | empty | 0 | 1006762 | 1010147 | 60436 | 2048 |  |
| 21 | warm | empty | 0 | 1131362 | 1134313 | 60436 | 2048 |  |
| 22 | warm | empty | 0 | 1215611 | 1218695 | 60436 | 2048 |  |
| 23 | warm | empty | 0 | 1259368 | 1262487 | 60436 | 2048 |  |
| 24 | warm | empty | 0 | 1284454 | 1281065 | 60436 | 2048 |  |
| 25 | warm | empty | 0 | 1195464 | 1198833 | 60436 | 2048 |  |
| 26 | warm | empty | 0 | 1258033 | 1261114 | 60436 | 2048 |  |
| 27 | warm | empty | 0 | 1313688 | 1260321 | 60436 | 2048 |  |
| 28 | warm | empty | 0 | 1208399 | 1211381 | 60436 | 2048 |  |
| 29 | warm | empty | 0 | 1305455 | 1257256 | 60436 | 2048 |  |
| 30 | warm | empty | 0 | 955121 | 957549 | 60436 | 2048 |  |
| 31 | warm | empty | 0 | 760005 | 762230 | 60436 | 2048 |  |
| 32 | warm | empty | 0 | 748345 | 750621 | 60436 | 2048 |  |
