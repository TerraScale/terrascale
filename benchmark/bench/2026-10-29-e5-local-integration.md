# E5 local integration verification — 2026-10-29

The combined preview builds TerraBase and its Fast, Slow and E2E projects after removing replication. The retained local Shapes, catalog security, backup and storage integrations run against real listeners and Turso files. Object-store traffic uses the existing integration transport fixtures.

Environment: delta, NixOS, linux-x64, .NET SDK `11.0.100-rc.1.26425.128`. Fast uses its existing maximum of 16 parallel tests. Elapsed times include test setup and shutdown.

Sources: E5 `456927c96`, based on main `296430e8a`; temporary integration with E4a `62784e2667d26a0b07c01e1466547cff418d0b93`; SDK `2dda37f08abe7497b35dc431895960e70f3c3611`. The dependency is merged only in a disposable managed checkout. Required PR CI remains the merge gate.

| Verification | Passed | Failed | Skipped | Elapsed |
|---|---:|---:|---:|---:|
| TerraBase build, including Fast, Slow and E2E | pass | 0 errors | — | — |
| Vendored driver regressions | 44 | 0 | 0 | 14.246 s |
| TerraBase Fast | 2,267 | 0 | 0 | 168.930 s |

The earlier Fast run exposed an observation race when advancing the test clock two days: the real daily backup scheduler could reserve a copy workspace while the cleanup assertion sampled it. The test now waits for the completed scheduled snapshot through HTTP, then bounds the wait for empty scratch storage and exactly zero reserved disk. Both strict assertions remain.

| Independent cleanup repeat | Passed | Failed | Skipped | Elapsed |
|---|---:|---:|---:|---:|
| 1 | 1 | 0 | 0 | 2.425 s |
| 2 | 1 | 0 | 0 | 2.613 s |
| 3 | 1 | 0 | 0 | 2.608 s |
| 4 | 1 | 0 | 0 | 2.661 s |
| 5 | 1 | 0 | 0 | 3.423 s |

```sh
nice -n 19 make terrabase-build terrabase-test-fast
for e5_repeat in 1 2 3 4 5; do
  nice -n 19 dotnet run --project tests/TerraBase.Fast --configuration Debug --no-build -- --treenode-filter '/*/*/SelectiveRecoveryTests/Out_of_retention_points_are_refused_even_when_objects_remain' --minimum-expected-tests 1
done
nice -n 19 make terrabase-e2e-local
```

The E2E-only follow-up on beta uses E5 `a24f6eac6` with E4a `46a9c1fc77b089f7ac20b014038e2b7a86a20c0a`. It passed all three local scenarios with zero skips in 6.369 s: immutable mirrored Shapes ciphertext/private live tail; acknowledged local writes through manual and PITR restores; and detection of an unchanged body rewritten with a new version. The fixture verifies its project binding before data requests and waits for the observed HTTP Date to reach the checkpoint boundary before shipping. The qualification checks still require matching bodies and versions, successful supported restores and a typed refusal outside the window. The timing fix also passed five independent repeats on mariogk-dev, each starting new isolated listeners and Turso files:

| Independent local E2E repeat | Passed | Failed | Skipped | Elapsed |
|---|---:|---:|---:|---:|
| 1 | 3 | 0 | 0 | 9.400 s |
| 2 | 3 | 0 | 0 | 9.311 s |
| 3 | 3 | 0 | 0 | 8.979 s |
| 4 | 3 | 0 | 0 | 9.026 s |
| 5 | 3 | 0 | 0 | 9.118 s |

```sh
nice -n 19 make terrabase-e2e-local
for e5_repeat in 2 3 4 5; do
  nice -n 19 make -o terrabase-build terrabase-e2e-local
done
```

The repeated E2E runs build once and reuse those exact artifacts; each process still starts new isolated test resources. No skipped scenario is accepted by the local gate. These previews do not claim exact-head GitHub CI or main verification.
