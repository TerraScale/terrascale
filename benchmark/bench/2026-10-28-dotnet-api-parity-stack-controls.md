# Split .NET parity runner qualification, 2026-10-28

The runner qualified at `a246ae193` is split into buildable review layers on document foundation `44f6c47a8`. Existing historical reports retain their original source hashes. These are the new heads actually checked on delta:

| Layer | Branch | Head | Changed lines | Existing `make check` |
|---|---|---|---|---|
| Lanes and counted transport | `fix/api-dotnet-parity-lanes` | `6eccb088e` | +210/−3 | 149 passed |
| Readiness and cell gates | `fix/api-dotnet-parity-cell-gates` | `36e779c87` | +293 | 172 passed |
| Owned run | `fix/api-dotnet-parity-owned-run` | `3ef2e20db` | +399 | 174 passed |
| CLI, preflight and lifecycle | `fix/api-dotnet-parity-run-lifecycle` | `e4de50e8a` | +321/−3 | 194 passed |
| Historical evidence | `fix/api-dotnet-parity-run-evidence` | `4ab31ad8b` | +91 | 194 passed |

Every run used `TS_HEAVY_HOST=delta ts-heavy-any nice -n 19 make -C benchmark/clients/dotnet check --repo terrascale --branch <branch>`. Each passed SDK preparation (official v0.0.18, `36d114877a69d18fba33b27ad362bd3e68e9e7ff`), locked restore, Release build with warnings as errors, all three format checks, and the parallel tests above, with zero failed or skipped tests. No beta work, production requests, credentials, tenants or live performance measurements were used.

| Retained delta log under `~/parity-runner-logs/` | SHA-256 independently rechecked by root |
|---|---|
| `stack-lanes.log` | `8d6e2c9a94b99708e4cd3e3b734ebdf181b6b32d1a89156d02e0698d0bd28252` |
| `stack-cell-gates.log` | `bcfada06d194a5265249c0a3aa0a60de130b68294ae9a20266a4fc232b57d762` |
| `stack-owned-run.log` | `368d77ddbdef511bb78bef0fdcce6b8d25a2cce7d36841aa3dedf199235a534e` |
| `stack-run-lifecycle.log` | `acb0c34d9977274d2c798e3d2cd9640854b4a86e08c4240e4d0f005048641d92` |
| `stack-run-evidence.log` | `33878aa5040e31ca3af2336b269bc9686662cf4a1761590abd6c57edb1d0b50d` |

Root independently verified the old and new changed-path sets differ only by added `ParityLane.cs` and `ParityLaneTests.cs`. The original `ParityCell.cs` class bodies equal the new lane and cell bodies concatenated, byte for byte. The counted-connect test and its server helper move unchanged into `ParityLaneTests`; all other scoped changed files match `a246ae193` exactly. The rebased foundation also contains already merged main-branch evidence outside this runner scope.

Intermediate controls arrive with their corresponding layers: the first layer checks TCP counting, the second exercises lane reads, and the fourth adds credential and late-cancellation controls. The final tree preserves all 194 tests and the review corrections. This report is added after qualification; no code changes follow the checked final head.

No required PR CI or live qualification is established by these local checks. Internal wire replays and remote erasure remain unverified as documented; there is no speedup or server-over-engine result.
