# E4a/E5 node backup isolation and Auth integration proof

Source: `bfa0ee3d0c2d32d487fe761c5055b5608a933112`, represented on the managed runners by remote head `232b9b8c15df03465325c7eebe862aa75caa80d5` plus its exact block-body correction. The correction fixes IDE0061 in the new regression; no assertion changes. Runs were admitted through `ts-heavy-any`, with `nice -n 19`, Release, locked restore and .NET SDK `11.0.100-rc.1.26425.128` on NixOS linux-x64.

`BackupNodeIsolationTests` starts independent real hosts/listeners and Turso directories sharing equal database IDs and one object-store integration fixture. Each repeat covers platform, customer and mirrored destinations. Divergent histories must restore their own data; retention on one node must preserve the other node's baseline and bytes. Progress, restore windows/plans and drills retain strict assertions.

| Repeat | Host | SDK revision | Backup passed | Failed | Skipped | Elapsed | Driver passed | Driver elapsed |
| --- | --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | mariogk-dev | `bcd20603f11f3aa54dc54cec6e410b4fe9e24151` | 3 | 0 | 0 | 17.552 s | 44 | 31.043 s |
| 2 | beta | `2dda37f08abe7497b35dc431895960e70f3c3611` | 3 | 0 | 0 | 6.985 s | 44 | 22.972 s |
| 3 | beta | `2dda37f08abe7497b35dc431895960e70f3c3611` | 3 | 0 | 0 | 5.932 s | 44 | 22.140 s |

The first backup-only invocation passed. Its subsequent full build stopped because that host's older sibling SDK lacked `TerraBaseClosureCancellation`; Auth did not run there. The beta run used an isolated archive of CI's pinned SDK, leaving the sibling checkout untouched. TerraBase's full solution filter and Auth's solution filter then built with zero warnings/errors.

| Auth validation on beta with CI's SDK pin | Passed | Failed | Skipped | Elapsed |
| --- | ---: | ---: | ---: | ---: |
| Full Auth Slow suite against the real TerraBase process | 25 | 0 | 0 | 30.733 s |

```sh
nice -n 19 make restore
nice -n 19 make -o restore terrabase-build auth-test-slow CONFIGURATION=Release \
  TERRABASE_BINARY="$PWD/src/TerraBase/bin/Release/net11.0/terrabase"
nice -n 19 make -o restore terrabase-test-fast CONFIGURATION=Release \
  TEST_ARGS='--treenode-filter /*/*/BackupNodeIsolationTests/* --minimum-expected-tests 3'
```

Local full logs: `/tmp/e4a-node-helper-proof.log` and `/tmp/e4a-ci-sdk-proof.log`. These focused runs are supplemental proof; they do not claim full combined-head GitHub CI. Required build, OpenAPI, Fast and local E2E checks remain the merge gate.
