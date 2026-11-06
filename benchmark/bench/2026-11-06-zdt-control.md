# Regional writer admission and recovery qualification: 2026-11-06

All six critical Slow drills passed five consecutive times against the complete
candidate: **30/30**, with no skipped tests. This is local protocol qualification;
no live rollout or latency acceptance pass is claimed.

The base is `5fd89c3e7271abc350125b0e68e18b4fd50f5f76`, plus the exact source hashes
in [the machine-readable proof](2026-11-06-zdt-control-proof.json). The runner
copied those source files into an isolated checkout through `ts-heavy-any`, using
the `terrabase-test-slow` admission label on beta (8 admitted CPUs, default test
parallelism). No candidate code was pushed to obtain this proof. Production
remained on b443 and the two stale global platform keys were untouched.

| Drill | Result | Durations, repeats 1–5 |
| --- | --- | --- |
| Foreign lease replay and guarded handback | 5/5 PASS | 9s 544ms, 8s 552ms, 8s 748ms, 8s 963ms, 8s 889ms |
| Target fails after CAS, before admission | 5/5 PASS | 20s 251ms, 19s 519ms, 19s 602ms, 19s 790ms, 19s 869ms |
| Controller disconnect at every phase | 5/5 PASS | 14s 243ms, 13s 785ms, 13s 406ms, 13s 950ms, 13s 873ms |
| NATS partition during rollback | 5/5 PASS | 15s 810ms, 15s 246ms, 15s 418ms, 15s 559ms, 15s 155ms |
| Both nodes SIGKILL during native write | 5/5 PASS | 14s 908ms, 14s 655ms, 14s 635ms, 14s 814ms, 14s 974ms |
| Manual owner CAS during native write | 5/5 PASS | 20s 442ms, 20s 455ms, 21s 459ms, 20s 917ms, 21s 001ms |

The target-failure recovery times were **11.671 s, 11.539 s, 11.787 s, 11.738 s, 11.802 s** against a
**30-second bound**. Timing starts before preparation and the owner CAS, so it is
stricter than timing from the CAS alone. The fixture blocks the target's first
exclusive-admission publication and verifies the key is absent. It then kills
the source handler first and the target second, confirms the persisted owner is
still unactivated on the target, and restarts only the retained source. No live
drain handler or controller can perform rollback. Refresh recovers automatically
from the durable activation deadline, reconciles all three acknowledged IDs,
accepts a new write and reconciles the resulting four IDs.

The other checks establish:

- Foreign platform leases replayed before startup and while watching never enter
  the cache or change local placements. Global keys stay unchanged. Promotion
  and handback reconcile every acknowledged row. A reverse stop is rejected
  until the old source has a new process session and catches up.
- Controller disconnection before reservation, before owner CAS, after owner CAS
  and after activation cannot authorize both stops. The last case leaves a
  durable retiring intent while the surviving writer accepts a new row.
- The rollback drill partitions the source's actual TCP connections to the real
  fixture NATS server. It waits for the bounded rollback failure warning,
  confirms fencing and the persisted phase, heals the transport, then requires
  the recovery CAS and phase clearance before write admission. This models lost
  quorum access; it does not simulate a three-server Raft election.
- Both processes are SIGKILLed with a native transaction open before COMMIT.
  After restart all ten acknowledged IDs remain, the interrupted row is absent,
  a source write succeeds immediately after writer readiness, and promotion
  preserves that row too.
- An uncertified owner CAS is injected while a native write is held. The old
  exclusive lease expires, the target rejects an actual SQL insert with 503,
  and the old transaction is refused before COMMIT. Automatic recovery retains
  the three acknowledged IDs and accepts a new write; neither rejected ID exists.

Fast validation also passed: **66/66 TerraBase**, 10.020 s measured process wall
(8.411 s TUnit duration; slowest test 3.687 s), and **4/4 driver**, 5.954 s wall
(slowest test 0.047 s). Both assemblies ran in parallel under the existing
30-second suite and 10-second single-test gates.

Reproduction, through `ts-heavy-any --repo terrascale --branch
zdt-region-only-proof-fe58d36d`, after applying the recorded source snapshot:

```sh
make restore turso-native
dotnet build tests/TerraBase.Slow --configuration Debug --no-restore
for zdt_repeat in 1 2 3 4 5; do
  dotnet run --project tests/TerraBase.Slow --configuration Debug --no-build -- \
    --treenode-filter '/*/*/*Regional*Tests/*' --output detailed --no-ansi || exit $?
done
make terrabase-test-fast
```

Raw evidence on beta: `/tmp/zdt-six-drills-r5.log`; its SHA-256 and every patched
source hash are in the JSON proof. An earlier candidate also passed 30/30 before
the durable-recovery drill was strengthened. An earlier quorum drill failed
waiting for a buffered subprocess log; the fixture now captures the actual
warning through a test-only logger provider. Superseded runs are not counted as
final-source qualification. The original collision's negative control and five
isolation passes are in [the earlier report](2026-11-05-zdt-lease-isolation.md).

A read-only check at 00:09:14Z returned 200 from the global API, GRU, SJC, FRA
and auth health endpoints. No production mutation or watcher restart occurred.
All test directories, subprocesses, listeners and synthetic tenants are owned by
the fixtures and disposed. The previously owned live load account was deleted.

Release stays held for explicit parent approval. The initial transition from
legacy b443 still includes a legacy handoff without the new automatic recovery
and durable stop exclusion; it requires an approved coordinated window and is
excluded from the three acceptance rollouts. See [the protocol and transition
notes](../../docs/terrabase/regional-rollout-handoff.md). CI/Images remain disabled.
Stale-key deletion waits until every watcher runs the isolation fix. Three live
rollouts at the agreed load, p99 <500 ms, bounded recovery from 429s/backlog, and
the independent acceptance HANDOFF remain outstanding. Notice replay #1104 is
not covered by these drills.
