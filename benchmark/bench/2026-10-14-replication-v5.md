# V5: live 3-region qualification of Wave A (2026-10-14)

Product commit `a439e1d17ddcc43df9e714eeb3c6afc6dc9187bd` (origin/main). Image
`registry.fly.io/ts-vtest-tb:vtest-a439e1d17ddc@sha256:b2267cc04eb069432bcdaf204eaee536320d70b109ddeb61d4bbd51c22edc804`
(the self-contained `terrabase` published with the repo Dockerfile's flags on beta, layered on the test-only faults base:
`2026-10-14-replication-vtest/scripts/Containerfile.faults`). Cluster `ts-vtest-tb` (terrascale-previews-515),
shared-cpu-2x 512 MB, one machine + 1 GB volume per region: gru `891e70eb4d0378`, sjc `6835466b44e258`, fra
`48e4967a225338`. Tests: `tests/TerraBase.E2E` (`WaveAE2ETests`, T1's `ThreeRegionE2ETests` and `ReplicationMatrixTests`),
run from beta in pinned mode (`TERRABASE_E2E_FLY_REVISION`, every request `fly-force-instance-id`, identity checked
before and after). Faults run alone with `TERRABASE_E2E_FLY_OUTAGE=1`. Local = in-process nodes on beta.
Latencies are measured from beta through Fly's edge, under no controlled load: not benchmark numbers.

| Row | Scenario | Where | Result | Numbers | Harness commit / run (UTC) | Raw log (sha256) |
| --- | --- | --- | --- | --- | --- | --- |
| R1 | 201 is `remote_durable` and another region serves it on the first read | Fly, origin gru | PASS | 201 in 241 ms; first read held by sjc, fra (779 ms after send) | `9848dd70` 17:20Z | `evidence/raw/WaveAE2ETests-A_201_is_remote_durable.txt` (`1afa8f66…`) |
| R1 | same | Fly, origin sjc | PASS | 417 ms; held by gru, fra (782 ms) | same | same |
| R1 | same | Fly, origin fra | PASS | 499 ms; held by gru, sjc (757 ms) | same | same |
| R1 | same | local, 3 origins | PASS 3/3 | 109 / 109 / 105 ms (gru / sjc / fra) | `bba212ff` 17:14Z | beta `/tmp/v2v5-local-5/WaveAE2ETests.log` (`d103718a…`) |
| R2 | Peer unreachable: defined 503, never a 2xx | Fly, gru cut from sjc + fra (ip6tables, both ends) | PASS | 503 `write_outcome_unknown` after 7,639 ms | `c4341eeb` 17:31Z | `evidence/raw/WaveAE2ETests-A_write_with_no_reachable_peer.txt` (`87961e6b…`) |
| R2 | same | local, gru partitioned | PASS | 503 `remote_region_unavailable` in 18 ms, document absent | `bba212ff` 17:14Z | local log above |
| R3 | Origin SIGKILLed right after its 201: the write survives | Fly, `flyctl machine kill` gru | PASS | sjc and fra serve it 1,382 ms after the kill call; gru back (restart policy) and serving it 20,850 ms after | `c4341eeb` 17:32Z | `evidence/raw/WaveAE2ETests-A_201_survives_the_origin_machines_SIGKILL.txt` (`125d2590…`) |
| R3 | same | local | not run here | `RemoteAckE2ETests` (separate processes) covers it on main | — | — |
| R4 | A7 drop safety: gru drops while cut from sjc; sjc writes after; heal | Fly | PASS | all 3 list exactly the post-drop write (1 doc, `819FCE3E…`) 311 ms after the heal; the pre-drop document is gone | `c4341eeb` 17:31Z | `evidence/raw/WaveAE2ETests-A_write_made_after_a_replicated_drop.txt` (`af5fa664…`) |
| R4 | same | local | PASS | 1 doc, equal digest in all 3, 1,008 ms after the heal | `bba212ff` 17:14Z | local log above |
| R5 | Write replicates, 6 directions (T1 `Write_in_one_region_is_readable_in_the_other_two`) | Fly | PASS 6/6 | median lag (n=10): SJC→GRU 23.8, FRA→GRU 26.1, GRU→SJC 223.7, FRA→SJC 228.5, SJC→FRA 292.3, GRU→FRA 298.3 ms | `9848dd70` 17:20Z | `evidence/raw/ThreeRegionE2ETests-Write_in_one_region_is_readable_in_the_other_two.txt` (`02d63335…`) |
| R6 | Delete + tombstone replicate, 6 directions (T1 `ReplicationMatrixTests`) | Fly | PASS 6/6 | SJC→GRU 28, FRA→GRU 23, GRU→SJC 228, FRA→SJC 223, SJC→FRA 295, GRU→FRA 298 ms | `9848dd70` 17:20Z | `evidence/raw/ReplicationMatrixTests-Delete_and_tombstone_replicate_to_both_other_regions.txt` (`57742cfd…`) |
| R7 | Forged peer: internal request without / with a wrong peer credential from another 6PN host is refused (A1) | Fly, operator WireGuard peer via `flyctl proxy` to each machine's 8081 | **FAIL, BLOCKED on #271** (enforcement not merged) | all 3 machines, both cases: forged operator write `POST /v1/management/organizations` → **201** (6 forged orgs created); gRPC `Replication/Digest` reached its handler (`grpc-status: 3`, not 16); `unmatched` counter +7 per machine (gru 8→15, sjc 4→11, fra 4→11) | `9848dd70` 17:18Z | `evidence/v5-forged-peer-a439e1d1.txt` |
| R8 | Rebuild (A6): corrupt one region's DB → rebuilt, converges | — | **BLOCKED** | #272 and #288 open; no production trigger on main | — | — |

## Reruns at newer SHAs

After #271 (and #272/#288) merge: `vtest.sh deploy <sha>` on a ts-heavy host (publish there, image layered on Fly's
builder, rolled to the 3 machines), then the same two commands (`e2e.sh` under `vtest.sh with-key`, faults with
`TERRABASE_E2E_FLY_OUTAGE=1`) and `forged-peer.sh`. **Operator calls need the peer credential once #271 is deployed**
(`vtest.sh bootstrap` and `with-key` mint keys on the internal listener): the secret exists only in Fly's store, so
before that rerun rotate it with a fresh value held only in the calling shell (or mint the key before the deploy).

## Limits

- R2 on Fly answers `write_outcome_unknown` (committed in gru, no acknowledgement within its wait), locally
  `remote_region_unavailable` (refused before execution): both are the documented 503s; the test accepts either.
- R3's timings include the `flyctl machine kill` call itself.
- Partitions are `ip6tables` drops inside the machines (faults image only); Fly Proxy still reaches every machine.
