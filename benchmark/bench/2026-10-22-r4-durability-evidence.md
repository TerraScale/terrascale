# R4 retained validation excerpts

Selected diagnostics only; service payloads, full metrics dumps and request details are omitted. Original files remain on beta in `/home/mariogk/.cache/`. These are local validation records, not CI results.

## Regression and green-test excerpts

### r4-ack-red.log

```text
failed An_ack_after_the_deadline_cannot_confirm_when_the_timeout_callback_is_delayed (6s 445ms)
  TUnit.Engine.Exceptions.TestFailedException: [Test Failure] AssertionException: Expected to be equal to ServiceUnavailable
  but received Created

Test run summary: Failed!
  total: 1
  failed: 1
  succeeded: 0
  skipped: 0
  duration: 6s 745ms
```

### r4-lease-red.log

```text
failed An_observed_lease_loss_changes_epoch_without_a_membership_change (2s 722ms)
  TUnit.Engine.Exceptions.TestFailedException: [Test Failure] AssertionException: Expected to not be equal to 2
  but received 2

failed A_disappearing_predecessor_does_not_bypass_the_fence (2s 739ms)
  TUnit.Engine.Exceptions.TestFailedException: [Test Failure] AssertionException: Expected to be null
  but found 2

Test run summary: Failed!
  total: 2
  failed: 2
  succeeded: 0
  skipped: 0
  duration: 3s 245ms
```

### r4-relevant-green.log

```text
Test run summary: Passed!
  total: 22
  failed: 0
  succeeded: 22
  skipped: 0
  duration: 10s 637ms
Test run summary: Passed!
  total: 46
  failed: 0
  succeeded: 46
  skipped: 0
  duration: 40s 222ms
```

### r4-backup-green.log

```text
Test run summary: Passed!
  total: 1
  failed: 0
  succeeded: 1
  skipped: 0
  duration: 6s 438ms
```

## Focused builds and self-checks

`r4-final-micro-before-build.log`

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Self-check PASS: 10,000 parallel attempts admitted exactly 4,096; cancellation plus lease disposal allowed refilling 256 database slots; the database bound rejected slot 257.

`r4-final-micro-after-build.log`

```text
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

Self-check PASS: 10,000 parallel attempts admitted exactly 4,096; cancellation plus lease disposal allowed refilling 256 database slots; the database bound rejected slot 257.

## R0 failed-run retained metadata

The completed first A result was overwritten by the retry; it is excluded. The two failed-run JSONs below are retained, including the incomplete sample; no incomplete sample enters the focused measurements.

### r4-loopback-after-failed.json

```text
commit: bfe7d7ccc2edfe566ed4d6d9b5210508cac98804
tree: 8471c5cdad65d515cea108eb49821f17874b77c4
started: 2026-10-22T13:46:13.430494128Z
finished: 2026-10-22T13:47:41.447617134Z
configured repeats: 5
recorded samples: 5
error: visibility write sjc failed: HTTP 503
visibility sjc->gru: document missing/read failed: HTTP 0
visibility sjc->fra: document missing/read failed: HTTP 404
visibility sjc->gru: no successful samples
visibility sjc->fra: no successful samples
```

### r4-loopback-before-failed.json

```text
commit: a4520b6cd4e4045b2e2e689e6fe3b1ac6cb07724
tree: d1f2f2931dd72b7489e0a85329121c2e6af1ffc7
started: 2026-10-22T13:49:11.667563676Z
finished: 2026-10-22T13:50:02.841527189Z
configured repeats: 5
recorded samples: 3
error: visibility write fra failed: HTTP 503
visibility fra->gru: document missing/read failed: HTTP 404
visibility fra->sjc: document missing/read failed: HTTP 0
visibility fra->gru: no successful samples
visibility fra->sjc: no successful samples
```

## Local original-file SHA-256 inventory

| File (beta cache) | SHA-256 |
| --- | --- |
| r4-ack-red.log | `a96b6caf9c25fea6e4d075b9ff8d0ed72da8f7a3c4c4d22fcd828bc18a0018a6` |
| r4-lease-red.log | `2788bf53d87520a518b68a21af33eccf304cb3a0ed61a0f904b503705e200945` |
| r4-relevant-green.log | `a38a7162fa7065ce02c229f5ecdc534c37063c87160a6b43cdee6f27b1476523` |
| r4-backup-green.log | `96bb2016b290e3e9a7e6acde01c5cbc616f128a0c3f0303e2433f1bdd67dbc45` |
| r4-final-micro-before-build.log | `c4890319b4db1b827fc59b86126fc8d251eec67cc89e7ea765fb4603f279c03f` |
| r4-final-micro-after-build.log | `0de95f3a800ed2953e97021b88d7990095c946723644a2fd8f7788e2fd378ebf` |
| r4-final-micro-before.md | `365ed83c01b54aebeb4574ab98c6c5e820a597e0ea311175a68e67ba6297544f` |
| r4-final-micro-after.md | `9f2589158be3079a8063177d7f1773ec2ab2650e09603db053962ca2a5e13899` |
| r4-loopback-before-failed.json | `1410ef98b7d003016e08129257f1551f9fc7492b9fadb89f56cf2767e833d5db` |
| r4-loopback-after-failed.json | `1375f1ed8119c3a5ea3380f83c6fb27ecf6528230d6e11940d0217cbcde48e24` |

The focused output files completed at 13:51:11Z (A) and 13:51:14Z (B), from their filesystem modification timestamps. The later host `/proc/loadavg` snapshot at 13:51:38Z was `11.63 18.42 22.77`; it is not a synchronized focused-workload sample.
