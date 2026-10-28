# .NET parity document controls, 2026-10-28

Source `431400cba`, stacked on timing controls `6cb254fa0`. SDK:
ts-dotnet v0.0.18, `36d114877a69d18fba33b27ad362bd3e68e9e7ff`.
No live call, tenant or credential was used.

| Check on beta | Result |
|---|---|
| SDK preparation fixtures | 14 passed |
| Locked restore, Release build and three-project format checks | Passed |
| All offline tests | 148 passed, 0 failed, 0 skipped |
| SDK checkout and committed dependency locks after checks | Clean, unchanged |
| Exact seeded SDK model and raw response, 256 and 4096 UTF-8 bytes | Accepted |
| Wrong id, version, state or same-length body | Rejected |
| Missing explicit state or document, null, malformed or trailing JSON | Rejected |

Raw responses deserialize with source-generated metadata for the SDK's public
document record. Both lanes must match id, seed-write version, explicit `value`
state and exact compact document text. These controls do not establish transport
behavior or qualify a complete live runner.

Command: `TS_HEAVY_HOST=beta ts-heavy-any nice -n 19 bash
/tmp/perf-api-dotnet-documents-qualification-a.sh --repo terrascale --branch
fix/api-dotnet-parity-documents`. The retained script runs existing `make check`
and verifies SDK cleanliness and unchanged locks. Artifacts on beta:
`~/remote-builds/terrascale/fix-api-dotnet-parity-documents-f8a77e69967a1fb4054dd77057000bca611fde68acf71c7be0cf9ec8faf5345e/benchmark/bench/api-dotnet-documents-qualification-a/`.
`check.log` SHA-256:
`bf2802087c7e89da315ed971f14091605704b91f4bc66d8879f915346e8072b4`.
