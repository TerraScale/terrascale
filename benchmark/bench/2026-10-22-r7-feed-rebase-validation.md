# R7 rebase validation after neutral edge routing (2026-10-22)

Source: `cda1d32ec1e43c3e6597ef986f15305c98a0b5b2`, rebased onto `adbbbe66c9f8e68aacc9335d68acd276d5847407`. Managed host: `delta`. The slot used unpinned `ts-heavy-any`; every build/run used `nice -n 19`.

Current main adds `IMembership` to the change-feed service for `ts-replay` routing. With the old benchmark fixture, the actual readers command failed before admission with `Unable to resolve service for type 'TerraBase.Engine.Cluster.IMembership'`. Registering the existing cluster services fixes the fixture; production routing is unchanged. The subsequent nine runs admitted every requested reader, validated every expected delivery and completed independent cancellation.

This is functional rebase validation, not a paired speed comparison. No performance improvement is inferred from these samples. The same-host A/B results remain in [the original report](2026-10-22-r7-feed.md).

Command: `nice -n 19 dotnet run --project benchmark/dotnet/FeedCursorBench --configuration Debug -- readers`. Fail-first finished at 23:56:20Z; corrected runs finished at 23:56:32Z. The program reports process costs and direct-service latency; it excludes HTTP, authentication, networking and replication.

| requested | admitted | rejected | repeat | delivered | all-path SELECTs | all-path SELECTs/write | feed SELECTs | feed SELECTs/delivery | CPU ms | CPU us/delivery | bytes/delivery | p50 ms | p99 ms |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | 1 | 0 | 1 | 200 | 2000 | 10.000 | 1200 | 6.000 | 861.827 | 4309.135 | 214136.2 | 3.795 | 8.436 |
| 1 | 1 | 0 | 2 | 200 | 2000 | 10.000 | 1200 | 6.000 | 821.242 | 4106.210 | 214105.8 | 3.747 | 6.087 |
| 1 | 1 | 0 | 3 | 200 | 2000 | 10.000 | 1200 | 6.000 | 841.002 | 4205.010 | 214104.1 | 3.818 | 7.305 |
| 10 | 10 | 0 | 1 | 2000 | 2000 | 10.000 | 1200 | 0.600 | 1065.716 | 532.858 | 30457.2 | 3.983 | 7.460 |
| 10 | 10 | 0 | 2 | 2000 | 2000 | 10.000 | 1200 | 0.600 | 1308.669 | 654.335 | 30474.6 | 3.921 | 7.036 |
| 10 | 10 | 0 | 3 | 2000 | 2000 | 10.000 | 1200 | 0.600 | 1289.607 | 644.803 | 30469.6 | 3.550 | 5.931 |
| 100 | 100 | 0 | 1 | 20000 | 2000 | 10.000 | 1200 | 0.060 | 1830.703 | 91.535 | 11977.5 | 3.514 | 7.081 |
| 100 | 100 | 0 | 2 | 20000 | 2000 | 10.000 | 1200 | 0.060 | 1488.746 | 74.437 | 11960.7 | 3.501 | 7.955 |
| 100 | 100 | 0 | 3 | 20000 | 2000 | 10.000 | 1200 | 0.060 | 1426.981 | 71.349 | 11962.0 | 3.416 | 8.083 |
