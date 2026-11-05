# Regional-only lease isolation: 2026-11-05

Base revision: `5fd89c3e7271abc350125b0e68e18b4fd50f5f76`.
Host: delta, admitted through `ts-heavy-any`. Uncommitted source was copied into
an isolated checkout without a push or production operation. Exact file hashes
are recorded below. This qualifies review item 1; it does not qualify the later
activation, writer lease or durable drain changes.

The Slow fixture replays auth's exact platform tenant/ID before startup, seeds the
platform once writer readiness is reached, then syncs the follower catalog. It
injects foreign dashboard and regional-only customer leases, uses an ordinary
replicated lease as an ordered watch barrier, and checks cache exclusion, local
placements, denied cross-region takeover, successful local preparation,
promotion, handback and acknowledged-row preservation. All ignored global keys
must remain unchanged.

| Repeat | Passed | Failed | Duration |
| --- | ---: | ---: | ---: |
| 1 | 1 | 0 | 3.963 s |
| 2 | 1 | 0 | 4.773 s |
| 3 | 1 | 0 | 5.370 s |
| 4 | 1 | 0 | 5.554 s |
| 5 | 1 | 0 | 3.816 s |

Negative control: identical test source, with the base revision's `HomeLeases.cs`.
It failed in 3.127 s at `leases.Find(PlatformDatabases.AuthId).IsNull()` because
it cached SJC2's foreign auth lease, epoch 3. Exit 2 was required and observed.
No production key was deleted to obtain this result.

Command, under `ts-heavy-any --repo terrascale --branch zdt-region-only-proof-fe58d36d`:

```sh
make restore turso-native
dotnet build tests/TerraBase.Slow --configuration Debug --no-restore
dotnet run --project tests/TerraBase.Slow --configuration Debug --no-build -- \
  --treenode-filter '/*/*/*RegionalHandoffTests/*' --output detailed --no-ansi
```

Exact positive-source SHA-256:

- `src/TerraBase/Features/Replicas/HomeLeases.cs`: `9d87132f1d76e62330fe39691617f183476e5adb625f3a7fe70703e3d273f4c5`
- `tests/TerraBase.Slow/Sync/RegionalHandoffTests.cs`: `09ceb5b5a4efa7b138cbfc21ceefce226a4d1a656e5c2368cbbd28bcf60f3ec7`

Raw logs on beta: `/tmp/zdt-item1-final.log` and `/tmp/zdt-item1-negative.log`.
Earlier fixture failures (missing platform seed and concurrent seeding/cleanup)
are recorded in issue #1124 and are not counted as passes.

Production placements remain unchanged; both stale global platform keys remain.
No deployment, workflow re-enable or live rollout is authorized by this report.
All six requested Slow drills still need qualification against the complete
recovery implementation before release approval.
