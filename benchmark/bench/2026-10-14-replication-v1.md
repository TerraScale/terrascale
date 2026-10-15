# V1: power loss never leaves a sealed shape chunk with a lost write (2026-10-14)

`tests/TerraBase.E2E/WalFaults/ShapeSealPowerLossTests.cs`: gru and sjc as separate processes with the WAL sync fault
shim. Remote durable writes are sealed into snapshot pages and chunks (shape HTTP API on the public listener, plus the
sealer probe's served and persisted chunk rows), then one tenant-log sync is killed: `write` (a gru write after the
seals), `seal` (a seal's own commit on gru), `acker` (sjc's apply of the next batch; sjc restarts with replication held
and is read back before it applies anything again). Unsynced bytes are dropped, the dropped frames but the last are
written back, and the last frame is torn after `torn bytes`. After restart every op is resolved against the document
value read back on gru (served ones also on sjc).

Columns: acknowledged = remote durable writes before the cut; recovered = those read back on gru; chunks checked =
probe served + persisted chunks + HTTP bodies after restart; ops checked = ops resolved (pre- and post-loss, with
repeats); lost = ops whose write did not survive (must be 0); retained = dropped bytes written back; HTTP resume =
the pre-loss handle at its pre-loss offset (409 `must_refetch` = handle rotated). PASS also requires no served chunk
changed range or body.

| run | cut | seed | acknowledged | recovered | chunks checked | ops checked | lost-write-in-chunk | unsynced bytes | retained bytes | torn bytes | HTTP resume | result |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| 2 | acker | 77 | 9 | 9 | 8 | 84 | 0 | 12360 | 8241 | 1 | 200 | PASS |
| 2 | acker | 9001 | 10 | 10 | 6 | 90 | 0 | 12360 | 10288 | 2048 | 200 | PASS |
| 2 | seal | 4242 | 11 | 11 | 6 | 75 | 0 | 16480 | 15360 | 3000 | 409 | PASS |
| 2 | seal | 13013 | 9 | 9 | 6 | 61 | 0 | 20600 | 16492 | 12 | 409 | PASS |
| 2 | write | 181 | 10 | 10 | 4 | 70 | 0 | 65920 | 61801 | 1 | 409 | PASS |
| 2 | write | 802 | 7 | 7 | 4 | 49 | 0 | 57680 | 55608 | 2048 | 409 | PASS |
| 3 | acker | 77 | 9 | 9 | 8 | 84 | 0 | 12360 | 8241 | 1 | 200 | PASS |
| 3 | acker | 9001 | 10 | 10 | 6 | 90 | 0 | 4120 | 2048 | 2048 | 200 | PASS |
| 3 | seal | 4242 | 11 | 11 | 6 | 75 | 0 | 16480 | 15360 | 3000 | 409 | PASS |
| 3 | seal | 13013 | 9 | 9 | 6 | 61 | 0 | 20600 | 16492 | 12 | 409 | PASS |
| 3 | write | 181 | 10 | 10 | 4 | 70 | 0 | 65920 | 61801 | 1 | 409 | PASS |
| 3 | write | 802 | 7 | 7 | 4 | 49 | 0 | 57680 | 55608 | 2048 | 409 | PASS |
| 4 | acker | 77 | 9 | 9 | 8 | 84 | 0 | 4120 | 1 | 1 | 200 | PASS |
| 4 | acker | 9001 | 10 | 10 | 8 | 93 | 0 | 12360 | 10288 | 2048 | 200 | PASS |
| 4 | seal | 4242 | 11 | 11 | 6 | 75 | 0 | 16480 | 15360 | 3000 | 409 | PASS |
| 4 | seal | 13013 | 9 | 9 | 6 | 61 | 0 | 20600 | 16492 | 12 | 409 | PASS |
| 4 | write | 181 | 10 | 10 | 4 | 70 | 0 | 65920 | 61801 | 1 | 409 | PASS |
| 4 | write | 802 | 7 | 7 | 4 | 49 | 0 | 57680 | 55608 | 2048 | 409 | PASS |

18/18 PASS (6 cases x 3 repeats), 0 lost writes in any chunk.

## Provenance

- Commit: `38812705edfa4238eb308114f2fb9af636a1415e` (branch `feat/repl-v1-seal-power-loss`), host `mariogk-dev`,
  .NET SDK `11.0.100-rc.1.26425.128`, Release build (`make terrabase-build CONFIGURATION=Release`, `make fmt-check`).
- Run command (3 repeats, through `ts-heavy-any` on mariogk-dev):
  `TERRABASE_E2E_RESULTS=~/v1-logs/runN.jsonl dotnet run --project tests/TerraBase.E2E --configuration Release --no-build -- --treenode-filter '/*/*/ShapeSealPowerLossTests/*' --output detailed --no-ansi`
- Raw logs (host-local, not committed), sha256:
  - `mariogk-dev:~/v1-logs/run2.log` `9ed21679810d25b0e1faaa5e9992f3071e0a94842307426247ae0ab2f5556831`
  - `mariogk-dev:~/v1-logs/run3.log` `eea50f1119c81d55761c647ab91718d3b468660bbe89f33ff7039b69460aed24`
  - `mariogk-dev:~/v1-logs/run4.log` `1a25332423663308c0468c18bac646662ff09a8089e5af2c920a88b098896017`
- Per-run numbers parsed from the evidence lines: `2026-10-14-replication-v1.json`.

Not covered: torn main-database pages after a checkpoint (the shim cuts logs only), power loss of both regions at
once, and Fly machines.
