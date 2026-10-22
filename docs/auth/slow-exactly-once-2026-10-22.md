# Slow auth security failure investigation, 2026-10-22

## Scope and boundaries

The six reports concern five concurrent auth workflows and real ts-auth access-token interoperability. The review
traced their callers through ts-auth's store and TerraBase's checked writes, rather than inferring safety from a
transport failure. No duplicate redemption, extra attempt, duplicate account owner or token-validation bypass was
observed. The concurrency guarantee reviewed here uses one authoritative TerraBase copy; these tests do not establish
linearizability across independently writable replicas. Multiple auth replicas share that authority.

Main at investigation start: `e7fa366ddb289f5612c93d316b316fb92c568151`.
Tested bounded-GC fix: `d2b2f07ac73250040c7717b3498944d8e8c3d775`.

## Five auth transport failures

Issues [#325](https://github.com/TerraScale/terrascale/issues/325),
[#326](https://github.com/TerraScale/terrascale/issues/326),
[#327](https://github.com/TerraScale/terrascale/issues/327),
[#328](https://github.com/TerraScale/terrascale/issues/328) and
[#330](https://github.com/TerraScale/terrascale/issues/330) report `HttpIOException: ResponseEnded` in
[run 38700457402](https://github.com/TerraScale/terrascale/actions/runs/38700457402). No report shows a failed
single-use or ownership assertion. The signing-key test fails during fixture seeding, before concurrent signing.

The test relay rejected connections above 64, while parallel races retained hundreds of pooled HTTP/1.1 connections.
Existing fix [`cd16a8a0`](https://github.com/TerraScale/terrascale/commit/cd16a8a0d0eddfff7cf9266768421521065322f4)
raises that bound to 1024 and adds a Fast regression retaining 128 public connections across two probes. This
investigation leaves every security assertion and production authentication path unchanged.

On unedited main, the auth concurrency filter passed 14/14 tests, zero skips, in 7.580 seconds on beta. On the tested
fix, all five exact test names passed in each of 20 consecutive Release rounds: 100 executions, zero failures or
skips. Each TRX was checked for exactly five executed/passed tests and the expected names. The 20 rounds took
184.711 seconds in total, with individual rounds ranging from 7.229 to 35.252 seconds.

An optional old-capacity/new-capacity control was canceled after more than 90 minutes queued and did not run.
The historical transport failure was not reproduced in this investigation; the classification relies on the
failure locations, the existing relay fix and its Fast regression, and the successful current-source repeats.

The reviewed security boundaries are:

- `A_correct_code_racing_wrong_guesses_wins_at_most_once_and_only_within_the_attempt_limit` (#325): both a wrong
  guess's increment and a correct guess's consumption require the exact current version. A lost comparison rereads
  state; consumption or the fifth failed attempt rejects later guesses.
- `Concurrent_account_creation_from_independent_stores_has_one_owner` (#326): normalized email ownership and the
  user record are created in one transaction. TerraBase checks absence within its writer transaction.
- `Concurrent_first_use_on_two_replicas_publishes_every_key_and_converges_on_one_signer` (#327): concurrent keys
  occupy separate records, and signer selection orders activation, creation and ID deterministically.
- `Concurrent_independent_stores_consume_a_challenge_exactly_once` (#328): tenant, purpose, digest, subject,
  consumption and expiry are checked before the exact-version consumed write. An ambiguous transport result grants
  nothing.
- `Every_issued_code_has_exactly_one_redemption_across_rounds` (#330): successful checked consumption precedes
  session creation, and the session transaction checks the user epoch. Each round still asserts one redemption and
  one additional persisted session.

## Real-token timeout

Issue [#154](https://github.com/TerraScale/terrascale/issues/154) times out while ts-auth issues its token, before the
TerraBase under test starts or validates it. In
[run 38391935489](https://github.com/TerraScale/terrascale/actions/runs/38391935489/job/115300897109), the shared
test process accumulated 405.586 seconds of GC pauses over 600.511 seconds. `AbandonedStatementTests` continuously
forced collections in that same process while ts-auth and its storage were running.

The fix collects and drains finalizers once per existing write round, after queuing the requests. All 60 rounds,
480 mixed requests and response assertions remain. Tests still run in parallel; auth timeouts and token validation
are unchanged.

On mariogk-dev, Release, with 46 server-GC heaps, the unedited paired baseline passed 2/2 but reproduced severe
interference: 152.251 seconds paused over 170.537 seconds of process runtime. The exact historical token timeout
was not reproduced, so this experiment does not establish GC interference as its sole possible cause.

Twenty consecutive fixed paired rounds passed: 40 executions, zero failures or skips. Each round's TRX was checked
for the exact real-token and failed-statement test names, executed/passed counts of two, and no skipped tests.

| Measurement | Unedited baseline | Fixed median | Fixed maximum |
|---|---:|---:|---:|
| Real-token test | 72.813 s | 6.678 s | 16.832 s |
| Failed-statement test | 170.458 s | 8.607 s | 30.643 s |
| Process GC pause | 152.251 s | 1.216 s | 8.147 s |

The token test retains signed-token issuer, audience, algorithm, type and subject checks, creates and lists its own
organization through the real management API, and rejects an anonymous client. Review found explicit signature,
issuer, audience, expiry, algorithm and access-token-type enforcement; no security defect was confirmed.

## Evidence and reproduction

Heavy runs used `ts-heavy-any` and `nice -n 19`, with no CI repeat loops or test parallelism reduction. The first
fixed paired round built through `make terrabase-test-slow CONFIGURATION=Release`; the remaining rounds reused
the same binaries. SDK source: `e7d9cf9a8c310f7c54940318b76253dd8dda7a81`.

The paired filter is:

```text
/*/*/*/(Failed_writes_never_leave_their_connection_to_another_thread)|(Access_tokens_from_the_real_ts_auth_authenticate_on_the_management_api)
```

Use `TEST_ARGS` with `-- --treenode-filter "<filter>" --minimum-expected-tests 2 --report-trx`, keeping both tests in
the same process. Repeat on a dev host, verify both exact test names and passed outcomes in each TRX, and retain
source/SDK hashes. A standalone token run does not reproduce shared-process GC interference.

Raw logs, TRX reports, source records and SHA-256 manifests are retained in the task worktree's ignored
`artifacts/auth-exactly-once/` directory. Copied artifacts were checked against their manifests. Manifest SHA-256:

- Five auth tests, 20 rounds, 103 checked files:
  `43ded7d8254cdd568aa12873149772bad46c35cef0a644aef21ac873c420221b`.
- Paired real-token/failed-statement tests, 20 rounds:
  `d0a84d4be207c73d552014c847e33d330155ca82765e161630cb08b5ba599cab`.

This is focused integration evidence, not a whole-Slow-suite result or a general proof of distributed auth safety.
