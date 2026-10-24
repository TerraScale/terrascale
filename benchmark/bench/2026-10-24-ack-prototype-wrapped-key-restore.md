# ACK prototype encrypted snapshot restore — five-repeat proof

PASS at `f344ed4a54edd2b7bd5e20223e20925d48d36757`: all five reported failing cases passed five times (25 executions), plus the missing-segment control five times. Total **30 passed, 0 failed, 0 skipped**.

The prototype now stores and reads the existing SnapshotBundle, preserving the wrapped database key alongside encrypted pages before roll-forward. The three-file fix leaves existing assertions, write counts, outages, deadlines and parallelism unchanged.

| Repeat | Passed | Failed | Skipped | Exit | Wall seconds |
|---|---:|---:|---:|---:|---:|
| 1 | 6 | 0 | 0 | 0 | 24.966 |
| 2 | 6 | 0 | 0 | 0 | 21.989 |
| 3 | 6 | 0 | 0 | 0 | 22.637 |
| 4 | 6 | 0 | 0 | 0 | 27.430 |
| 5 | 6 | 0 | 0 | 0 | 22.250 |

One mariogk-dev admission, one fresh Release Slow build (0 warnings/errors), one discovery, five executions, no retries. All six source-derived methods and exact discovered IDs matched every original TUnit JSON report; original HTML retained. Parallelism 16, nice 19.

Owned clean source at the tested literal, owned clean ts-dotnet `408044452f0a71c3725866bcc6b95603714acb1f`, SDK `11.0.100-rc.1.26425.128`; 46 CPUs, admission load [0.52685546875, 0.5947265625, 1.1708984375]. Runtime native normal download and actual loaded mappings verified SHA-256 `7464a29f3fc9bce603a1e0881720b309a276455a778937d6a9ffc2ed680bd76d`.

Original commands, exits, logs, JSON/HTML, identities, loaded maps, independent audit and process/lock release receipts are retained in this directory. This is targeted integration-test proof, not a full Slow suite or performance benchmark. PR #582 CI remains a separate gate.

Reproduction after native preparation and a Release Slow build:

```sh
nice -n 19 dotnet tests/TerraBase.Slow/bin/Release/net11.0/TerraBase.Slow.dll --maximum-parallel-tests 16 --treenode-filter "/*/*/AckAfterSegmentTests/*" --minimum-expected-tests 1 --output detailed --no-ansi
```

References: #575, #576, #578, #579, #580; failing main run 40079489279 at `fcbf41c9f`.
Evidence directory (dev and delta): `/home/mariogk/.t3/test-artifacts/database-encryption/ack-restore-f344ed4a5-dev-20261024T091749Z`.
76 evidence files independently checksummed on both hosts; manifest SHA-256 `6187a90e57fb34fd000d543ec008a5a996470973d1028e9522ad4bd05a95d523`. Owned processes and admission locks are free.
