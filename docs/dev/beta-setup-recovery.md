# Beta Fast setup recovery, 2026-11-04

Beta's ts-ship jobs were exiting normally with code 2 during the GitHub identity
check, before starting their Fast suites. They were not killed by systemd or OOM.
The original mariogk-dev log `20261104T200411-8f8c64ca.log` records GitHub HTTP 403
`API rate limit exceeded` from `gh api user` for TerraBase at 23:04:30Z, .NET SDK
at 23:04:35Z, and libraries at 23:04:42Z. Beta's ts-slot events record all three
exits as code 2. The user manager remained running; the job scopes were in
`app.slice`, outside `app-t3code-agents.slice`. No Homelab configuration changed.

The initial live reproduction used `TS_HEAVY_HOST=beta` and a throwaway branch
through `ts-heavy-any`, with ts-ship's actual `remote_fast_command`. It passed
libraries after GitHub's quota reset. Replaying the recorded HTTP 403 against
the original runner on beta reproduced exit 2 without an area failure marker.

## Installed fix

The installed ts-ship helper now uses `gh api --cache 1h user` for the Fast
identity check and carried-proof identity check. This removes repeated identity
requests during split Fast batches. A cold identity cache still needs GitHub;
the cache does not make status publication available during quota exhaustion.
Pending, failure and success statuses still require live publication, and the
returned status creator must still be MarioGK.

Top-level setup failures now emit `@@TS_SHIP_AREA setup` and a structured
`@@TS_SHIP_FAILED` record containing the actual GitHub error. A cold-cache quota
failure or failed status publication remains a failure and cannot produce a
success proof. This makes the reason visible instead of appearing to die after
setup.

The exact installed patch is
[ts-ship.patch](../../.cos/beta-setup-20261104/ts-ship.patch). Apply it to the
previous installed helper, SHA-256
`abb363b0c44e320e03a5a82fc0d53274249f1fab49b8ab4050fcc4a2ad759dd9`,
using `patch ts-ship < ts-ship.patch`, validate the Python syntax, then replace
`~/.local/bin/ts-ship` atomically. All three hosts retain the old helper as
`ts-ship.before-beta-setup-20261104`. The installed result on delta, beta and
mariogk-dev has SHA-256
`0988f2424d60fb9b78c7e8e271fd9d06ee5dc71caf1bc22e51d7823d6883fd1e`.

## Verification

Five beta identity reads succeeded with `HTTPS_PROXY` and `HTTP_PROXY` pointed
to an unreachable loopback port after one cache warmup. The
[failure replay](../../.cos/beta-setup-20261104/check-failures.py) separately
proved that an identity HTTP 403 reports setup failure, and a status-publication
HTTP 403 reports the suite failure; neither emits a success marker.

Five consecutive jobs used the actual `remote_fast_command` and `ts-heavy-any`
with `TS_HEAVY_HOST=beta`, on throwaway branch `wrk/beta-setup-repro-20261104`
at source SHA `48953f0a687e70f0691852774d5b308ae8a4ddb6`. Each job's admission
message confirms beta executed it; none transferred to a peer. Source-owned
parallelism and test selection were unchanged.

| Job | Area | Fast seconds | Build seconds | Result |
| --- | --- | ---: | ---: | --- |
| 1 | Libraries | 12.062 | 20.634 | Pass |
| 2 | .NET SDK | 7.156 | 21.160 | Pass |
| 3 | TerraBase | 24.201 | 61.469 | Pass |
| 4 | Libraries | 12.276 | 20.624 | Pass |
| 5 | .NET SDK | 7.235 | 13.342 | Pass |

Libraries ran 50 endpoint and 42 logging tests per job; the .NET SDK ran 266
tests on each of its two runtimes per job; TerraBase ran 5 SQL-driver and 92
service tests. All reported zero failures and zero skips. TerraBase waited
127 seconds for normal admission before starting; its complete job, including
queue, preparation and checked-build qualification, took 255.452 seconds.
The Fast execution clock above excludes those phases.

[Saved results](../../.cos/beta-setup-20261104/fast-results.json) and
[test summaries](../../.cos/beta-setup-20261104/fast-proof.txt) retain the proof.
After all five jobs passed, `~/.config/ts-host/drain` was removed and confirmed
absent on delta, beta and mariogk-dev. A subsequent `ts-host -j --label libs-test`
ranked beta first with `fits: true`, no running jobs and no queued jobs.
