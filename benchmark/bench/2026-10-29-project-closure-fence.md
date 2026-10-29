# Conflicting replicated erasure fence: regression proof

The CI/root shipment reproduced an `InvalidOperationException` from the organization-binding guard during convergence and server shutdown. A conflicting replicated fence must preserve local contents and keep closure pending.

The guard still checks the local organization binding before touching any database, API key or backup destination. A mismatch now returns non-empty evidence, so the existing receipt path records `Empty=false` and convergence remains usable. The HTTP endpoint still returns `409 binding_conflict` for the mismatched organization.

The existing Fast integration test retains its HTTP conflict and database-retention assertions and additionally requires a receipt naming the same closure with `Empty=false`. The stronger regression fails deterministically before the source change: the receipt is null after convergence. Its diagnostic used the real TerraBase host and catalog, not a mocked erasure implementation.

## Dev-host proof

Measured source fix `15c0a38440d9cabd1e53ad0bbf46f2beeb72aeba`, before rebasing onto unrelated main changes. `ts-heavy-any` selected mariogk-dev; host parallelism was 46. Each run is the complete `make terrabase-test-fast` target, including locked restore and build, with TRX reporting. All retained assertions remain; no tests were skipped.

| Full Fast run | Wall time | TerraBase | Driver | Result |
| --- | ---: | ---: | ---: | --- |
| 1 | 151 s | 406 | 24 | Passed |
| 2 | 95 s | 406 | 24 | Passed |
| 3 | 97 s | 406 | 24 | Passed |
| 4 | 82 s | 406 | 24 | Passed |
| 5 | 84 s | 406 | 24 | Passed |

## Shipped proof

The TerraBase source and integration tests are identical after rebasing onto main. Source fix shipped through `ts-ship` as `4a37fb4046108f797e0586cbc6e0a67eb8ae876e`. The final exact-commit gates passed: TerraBase **122.602 s**, Auth **162.386 s**, both below the 299-second deadline. Two earlier candidates also passed both gates and were rebased after unrelated main pushes.

Main push CI: [40973347603](https://github.com/TerraScale/terrascale/actions/runs/40973347603).
