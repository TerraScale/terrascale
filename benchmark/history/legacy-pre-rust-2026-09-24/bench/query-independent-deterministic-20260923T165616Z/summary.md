# Deterministic query-independent benchmark summary

Campaign: 2,000 documents, 30 repeat samples, maximum 2 indexes, and 100 post-build maintenance documents. Three fresh processes ran sequentially with the supplied Release artifact.

| Trial | Status repeat p50 (µs) | Nested repeat p50 (µs) | Build (ms) | Logical index bytes | Recognized WAL (B) | WAL staging (B) | Whole-root index delta (B) | Report | Receipt |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- | --- |
| 1 | 34.8 | 34.0 | 24461.0 | 1004100 | 104 | 0 | 449615 | [report](trial-1/query-independent-indexing.md) | [receipt](trial-1/run-receipt.json) |
| 2 | 33.6 | 31.7 | 26321.8 | 1004100 | 104 | 0 | 450251 | [report](trial-2/query-independent-indexing.md) | [receipt](trial-2/run-receipt.json) |
| 3 | 31.8 | 32.5 | 27489.8 | 1004100 | 104 | 0 | 451397 | [report](trial-3/query-independent-indexing.md) | [receipt](trial-3/run-receipt.json) |

status repeat p50 median/range: **33.6 µs** [31.8, 34.8].

nested profile.segment repeat p50 median/range: **32.5 µs** [31.7, 34].

## Verified invariants

- All three receipts are `complete` with exit code `0`; each report has eight expected rows.
- Deterministic TerraBase rows have 2 active indexes, result coverage `True`, and query history before candidates `0`.
- No-index and SQLite rows report `N/A` for the automatic-index logical/WAL/staging fields.
- SQLite explicit plans name `ix_users_status` and `ix_users_profile_segment`.
- Jev was not run: every report states `UNRUN: --jev was not supplied`; the invocation omitted `--jev` and used `env -u TERRABASE_JEV_API_KEY`.

## Artifact and source evidence

- Benchmark DLL: `/tmp/terrabase-bench-build-20260923-nosdk/bin/TerraBase.Benchmarks/Release/net11.0/TerraBase.Benchmarks.dll`
- Benchmark DLL SHA256: `1233089290f2255a067910d08d4890c216e599d7c15d22e483f5ee23a140283d`
- TerraBase dependency SHA256: `c310997c2ffb43d0b22ea3f1afe1ef3f0f3df33cd1d933094e771e366a2970a4`
- TerraBase.ZoneTree dependency SHA256: `085029bfafcdc9d5ff27d9b3a79c9325780f3e367a8cadb27c0d57b3cbc74799`
- TerraScale.Endpoints dependency SHA256: `7c26bc6966dd43108ca4a2dc26319658198565099bff4a86428ca0a5a8db6a09`
- Source commit: `67e35f443565c54eb731d6ea2dd989cb5c87b7be`; tree digest: `69203400f13c7e311cf5fb569a081eeab36e8cbc`; working-tree digest: `044d751eb6d7a0e2ee4582d7229a0be4a9474cc4a4d7f9beb65de52d8e26b111`.

## Caveats

- The trials were sequential and used fresh processes, but shared-host contention can affect timings. Treat the timing values as diagnostic; this campaign does not establish performance superiority.
- Recognized WAL and staging values are diagnostic subsets from `InspectStorageAsync`, not complete compaction cost or total historical I/O.

## Per-trial evidence hashes

| Trial | Report SHA256 | Console SHA256 | Receipt SHA256 field |
| ---: | --- | --- | --- |
| 1 | `18b418a420f4e47f988a0383a3d7b8786e85529c18f0f3cad3575d009305a1ec` | `1c913e55a4e97a7f846eb40b02e2f148618ff9a72c53bcd8457f01b59089c7b6` | `7a42bc24bfafb384eb8f3264e3245a4e5cb76a4128f26c20e8ef3869f6946a8e` |
| 2 | `5836a4f079efbc6b0b7615c42cc648a405d55121952daafa1ac68094268f734a` | `2a8b04d1ce841c727783d25287e37a712589a2eee9a341c952888f907107580b` | `9783d503d63729634c3bf671cdbe3962bcf496168061747cecc3dd77dbe2cb32` |
| 3 | `4f09dd53efe8c059f672e62a61ce2c65f6059d9cd2acb0c4b7fc9ffb67f02f77` | `0da282a365c6084eff37cf4a765c18847a7c1796b01c4783bccdbd00498598ff` | `cbfd38ce31b75dc7ef43e1ae7f46e1ac6169bf64c5c9af3282a2a54b29e89ade` |
