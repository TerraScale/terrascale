# Legacy bootstrap and regional recovery qualification

Local engine qualification passes **55/55 Slow executions** (11 drills ×5),
TerraBase Fast **66/66** in 9.395 s and native-driver Fast **4/4** in 0.389 s.
Candidate `7845bed259c0be3ad378d9f3d9d3c8519a0cc2c4` is unshipped. Production,
CI/Images, watcher restarts and stale platform-key deletion remain held.
This replaces the fixture-only bootstrap evidence in the earlier re-review report.

The [proof JSON](2026-11-06-zdt-bootstrap-proof.json) records every test name,
five durations, exact source hashes, raw-log hashes and retained evidence paths.
Qualification ran through `ts-heavy-any` on beta, with a real NATS server,
real TCP fault proxies and real native database files. The NATS faults do not
claim multi-server Raft-election coverage. No production account or load was used.

| Critical path | Result |
| --- | --- |
| Applied-position promotion and guarded handback | 5/5 PASS |
| Controller disconnect before reservation, before/after CAS and after activation | 5/5 PASS |
| Transport loss during persisted rollback | 5/5 PASS |
| Certified target versus connected source still holding admission; both attempt SQL commits | 5/5 PASS |
| Foreign, third-node and identity-mismatched DB lease isolation | 5/5 PASS |
| Writer killed inside an open native write; every acknowledged row preserved | 5/5 PASS |
| Target fails after acquiring exclusive admission, before activation finishes | 5/5 PASS |
| Target fails after owner CAS, before acquiring admission | 5/5 PASS |
| Certified live target retries instead of selecting a dead source | 5/5 PASS |
| Upgraded follower cannot claim a same-region home during owner restart | 5/5 PASS |
| Actual b443 Program upgrades on retained owner files with Version:0 metadata | 5/5 PASS |

The two failed-activation paths restored native writes within the stated 30 s
recovery bound. The maximum observed time was 12.508 s before target admission
and 12.701 s after target admission. Test wall times include fixture setup,
fault injection, canaries and cleanup; they are not recovery times.

## Real legacy process and an additional race found by the proof

The driver builds **unmodified b443 source**, exact revision
`b443b6ea83f519018c0d9f9d8f04e65c3df12724`, inside the monorepo on the dev host.
It runs its ordinary `Program` with `dotnet`, retained primary files and real
NATS/peer inputs. There are no fixture DI overrides or test endpoints inside
that legacy process. Its x64 Debug DLL SHA256 is
`b2fa60c461c4cd198821781ec21b44cdda2fe1350f3f281ccad900962f989d03`.
This proves actual legacy code, rather than a new runtime reading legacy-shaped
JSON. It does not qualify production arm64 packaging or its containerd install.

The real b443 process acknowledges two new rows, including one while its upgraded
follower runs. It is then killed and physically reaped before candidate startup.
During a 32 s owner absence, the follower remains unadmitted and the same-region
home lease's exact KV revision is unchanged. The candidate starts on the same
owner files and ultimately acknowledges another row. Final exact inventory
contains all six expected rows, with the original owner and epoch retained.

Earlier real-b443 attempts passed twice, then failed on repeat three: automatic
recovery treated the new preparation certificate on a Version:0 owner as
permission to select the follower. The observed record changed from GRU1 epoch7
to GRU2 epoch8. Both attempts are retained in `zdt-real-bootstrap-r2.log` and
`r3.log`; they are failures, not passes.

The fix excludes **every Version:0 record** from automatic transfer recovery,
even after preparation. Only the retained owner may finish legacy adoption.
The final drill deterministically blocks that owner's per-DB lease confirmation
after its preparation certificate, waits 12 s across the activation-recovery
deadline, and proves that owner/epoch remain pinned before allowing activation.
All five final repeats include this barrier. Their deliberately injected 32+12 s
waits are fault coverage, not an estimate of bootstrap maintenance downtime.

## Remaining review nits

Same-region failover claims are refused at expiry scanning, claim execution and
`TakeAsync`. Normal same-region authority belongs exclusively to regional handoff;
cross-region failover and R10 holds remain intact.

The identity drill seeds a same-region lease with another organization and a high
epoch. Activation isolates that single DB, preserves its lease and refuses its
writes while the healthy DB serves. Documentation states that death of an isolated
holder never permits overwriting that conflicting revision. Only a valid newer
authority can end the isolation.

After releasing the connected source's overlapping native transaction, the
manual-CAS drill opens its **local primary directly through IStorageEngine**,
before healing its owner reads. Its only rows are `ack-0`, `ack-1`, `ack-2`;
`source-overlap` is absent. The target inventory separately includes those three
rows and its acknowledged `target-committed`, with neither rejected overlap row.
This source inspection bypasses HTTP follower forwarding.

## Reproduction and release gates

On a dev host through `ts-heavy-any`, restore/build the candidate Slow project,
then execute `bash benchmark/regional-bootstrap/run.sh`. The script archives and
builds b443, records its DLL hash, runs all regional Slow drills five times, then
runs `make terrabase-test-fast`. The exact snapshot runner used for this report
and the raw outputs are retained on delta under
`.cos/zdt-20261105/qualification/`.

The separately qualified orchestrator fence uses real Swarm dispatch and OS
process exit, with HTTP/container-runtime contract fixtures. Its report is
`ts-orchestrator/docs/rollout-bootstrap-qualification.md`. Installing that fence
on old orchestrators without restarting vulnerable watchers is still an explicit
deployment gate; no tested hot installation or offline arming is claimed.
There is also no automatic fence removal: final desired-state reconciliation
must be qualified before normal rollouts resume.

The engine branch is based on `5fd89c3e7`; origin/main has advanced. Rebase and
affected qualification are required before any later ts-ship. The independent
review must cover the additional Version:0 guard and driver patch. A successful
local test does not authorize production execution. The three live zero-error,
p99 <500 ms rollout proofs, recovery-tail measurement and acceptance HANDOFF remain
outstanding. No notice durability claim or #1104 closure is made.
