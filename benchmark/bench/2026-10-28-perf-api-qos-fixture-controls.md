# QoS saturation fixture clock qualification, 2026-10-28

This is test qualification, not a latency comparison or production policy change.
The server-over-engine target and denominator remain unchanged and unmet.

Required Fast failed in [run 40684495546](https://github.com/TerraScale/terrascale/actions/runs/40684495546/job/122803416577):
`Hobby_saturation_preserves_a_worker_and_admission_for_paid_and_system_requests`
timed out after two minutes. Its second Hobby request had already returned 503
`storage_busy` when the global pool barrier completed. A later request then waited
behind the held worker. Background system admissions are a consistent explanation,
not a controlled reproduction of that failure; other sources of `storage_busy`
were not conclusively excluded.

## Diagnostic control

A temporary real-server probe at `2330e211508baaad60b27c6da70383d1bf4e7cc8`
performed the same fixture setup and observed eight seconds without client work.
Both clock cases used the default parallel test execution. Pool counters recorded:

| Clock | Submitted/completed storage calls | Busy admission samples | Maximum admitted |
|---|---:|---:|---:|
| Real | 176 / 176 | 29 / 3,651 | 1 |
| Frozen global clock | 0 / 0 | 0 / 3,643 | 0 |

This is one observation per clock, not a proof that all future event-driven work
is absent. The producer services were not attributed. The probe deliberately threw
to expose its counters in TUnit output; it is not in the publication branch.
An earlier probe failed to build on an unused using; the next passed without
visible captured counters. Those outcomes remain in the logs below.

## Fix and qualification

At source head `5f5c00014980687fc3c5a9b09d5a58c054e7ef6b`, the fixture installs
the existing `FakeTimeProvider` through `ManagementApi.StartAsync(time:)`.
The separate keyed clock still holds real Hobby query execution. The barrier now
also detects completion of that specific queued request and asserts it is pending,
so an early refusal fails promptly instead of being mistaken for another holder.

Every original assertion remains: third Hobby 503, paid query 200, explicit
`/readyz` 200 while both Hobby requests are pending, queued cancellation/drain,
and successful completion after releasing the first query. All services remain
registered. Timer-driven background progress during saturation is not exercised;
the system-progress assertion covers the explicit readiness request. No production
threshold, timeout, admission policy, retry, sleep or test parallelism changed.

On delta through `ts-heavy-any nice -n 19`, SDK `11.0.100-rc.1.26425.128` and
ts-dotnet `03881aa74a52683112180a5035d7b5ced8d65032`:

| Check at the source head | Result |
|---|---|
| `make terrabase-build` | Passed, 0 warnings/errors |
| Focused class, five predeclared runs | 2/2 passed in each; no skips |
| Full `make terrabase-test-fast`, admitted after all five passed | 3,161/3,161 passed; no skips |
| Turso driver tests accompanying each Fast invocation | 44/44 passed each time |

These used the Makefile's Debug default on main `05e7adb5b`. The publication test
file is byte-identical. Publication was rebased onto main
`ac630e4af06da8479119a1469f684b14bb63dfff`, which also includes later request-log
and auth-timing changes. No new local suite result is claimed for that base.
CI Release, formatting and required checks remain the merge gate.

| Retained delta log | SHA-256 |
|---|---|
| `/tmp/qos-quiet-4310e92.log` (build failure) | `cc75be6b0b6f94b8d7656aacdcea900c975e87b82e38b5fb59f4205762f40e55` |
| `/tmp/qos-quiet-35caa2f.log` (passed; counters not visible) | `94a26ac04e9b0adfa6139e7e21bd5f992d0ac83232ff6e5138c8af9395ee5ade` |
| `/tmp/qos-quiet-2330e21.log` (intentional diagnostic failure) | `cc6ae4b3b8428ddc07f4cff3304b96f536ec23a7287d45e41a0318f61b8b73ff` |
| `/tmp/qos-qualify-5f5c000.log` | `ea91d9cf9967435e19462a349f2359bfefc7b87e48a0f7c65b3d07cfb63f9710` |

No live tenant, credentials, production requests or deployment were used. This
does not resolve the historical owned database or qualify regional SDK parity.
