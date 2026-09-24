# 1M RustFS diagnostic stop

This is a preserved diagnostic stop record. It is not a completed 1M
qualification receipt.

## Stop state

| Item | Evidence |
| --- | --- |
| Run | `e2e-resource-rustfs-1m-final-20260923T215722Z` |
| Requested workload | 1,000,000 documents; S3 object tier; RustFS; 4 GiB cgroup; zero swap |
| Last completed phase | `index-build` was in progress; no index activation or query phase completed |
| Last resource sample | `2026-09-24T04:00:35.0192109Z` |
| Stop | Coordinator stopped the superseded workload around `2026-09-24T04:01Z` after the CPU bound index build showed no useful completion progress. The exact signal timestamp was not emitted by the run. |
| Run service state | `inactive/dead`, `Result=success`, `ExecMainStatus=0` after the coordinator stop |

## Completed evidence before the stop

- 1,000,000 bulk writes.
- 10,000 relationship writes and verification.
- Exact scans and snapshot paging, including the post mutation population of
  999,000 documents.
- 10,000 point reads.
- Full and incremental backups.
- 1,000 updates and 1,000 deletes.
- Remote journal source recovery, local root deletion recovery, and cold remote
  reopen.
- RustFS provider validation.

The root-loss checkpoint reports `status=complete`, `artifactsVerified=2`,
`artifactBytesVerified=112162262`, and `appliedLogEntries=2`.

## Interrupted index build

The latest retained ZoneTree metadata reports:

| Measure | Value |
| --- | ---: |
| `MaximumOpIndex` | 1,791,409 |
| Estimated physical row operations | 1,998,977 |
| Approximate build fraction | 89.62% |
| Exact snapshot population | 999,000 |

The index remained inactive. No indexed query benchmark, post index
compaction verification, or final `e2e-volume-receipt.json` was produced.

## Resource evidence

| Measure | Peak or final value |
| --- | ---: |
| Cgroup memory limit | 4,294,967,296 B |
| Cgroup memory peak | 1,721,520,128 B |
| Process RSS peak | 890,630,144 B |
| Swap limit/current | 0 B / 0 B |
| Local storage peak | 34,892,910,592 B |
| RustFS data peak | 33,515,507,712 B |
| RustFS object inventory at final sample | 161 objects / 1,425,167,097 B |
| Cgroup cumulative writes at final sample | 239,925,209,088 B |
| Cgroup OOM / OOM kill | 0 / 0 |

The final resource sample records `oom 0`, `oom_kill 0`,
`oom_group_kill 0`, and `SwapCurrent=0`.

## Frozen source and binary identity

| Item | Value |
| --- | --- |
| Source commit | `67e35f443565c54eb731d6ea2dd989cb5c87b7be` |
| Tree digest | `69203400f13c7e311cf5fb569a081eeab36e8cbc` |
| Working tree digest | `5c5a337f2fdf5fec04557248f69f3f1cbf031ad0a22fff19b7a67062aac5db3a` |
| Source dirty | `true` |
| .NET | `11.0.100-rc.1.26425.128` |
| Benchmark binary | `terrabase-final-source-matched-release-20260923T215453Z/TerraBase.Benchmarks.dll` |
| Benchmark SHA-256 | `9a8567448833276d091a459a65f0a70a0c5be114dc24395251ce9d49a1b9d913` |
| TerraBase SHA-256 | `8f9aa96e352197b8d2907c5e73f6a4527af4dd3b79d9fe3b2d69e64459051287` |
| TerraBase.ZoneTree SHA-256 | `58c8fd7dbf5521ba066463cca9a338301d5ae82999fb63597c9156f3db1a0aa0` |

The complete launch identity remains in the sibling
`e2e-resource-rustfs-1m-final-20260923T215722Z.launch/` directory.
