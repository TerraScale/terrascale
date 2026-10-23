# Continuous-backup failure audit — 2026-10-23

## Remaining shutdown defect (#265)

At main `49e32e99c`, final backup shipping's four-second deadline used the
injected `TimeProvider`, while the host's eight-second service deadline used
real time. A frozen fixture clock prevented the shorter deadline from firing.
When shipping waited for the catalog writer, the host canceled first and the
backup service escalated that cancellation as a cleanup failure.

The new blocked-writer integration regression failed before the fix, with the
same stack as #265: `AcquireWriterAsync`, `RecordAsync`, `RetainAsync`,
`ShipAsync`, `StopOnceAsync`. Its single selected test failed without skips;
the driver tests passed 22/22. Evidence on delta:
`/tmp/ts-backup-issue-audit/red.log` and
`/tmp/ts-backup-issue-audit/red/TerraBase.Fast_net11.0_x64.trx`.
The historical run linked by #265 (`38498891946`) now returns GitHub HTTP 404;
the issue preserves its redacted stack.

Final shipping now uses `TimeProvider.System`, matching the host deadline.
Exhausting the shipping budget ends this best-effort flush normally, then
disposes every tap. Caller cancellation and other errors still fail shutdown,
preserving the existing cancellation, tap-return, and directory-reopen tests.
No durability claim is added: unshipped data stays in local storage and is
included in a later snapshot.

The cached-customer-deadlines test previously performed a full PITR restore
while shutdown was blocked. That fixture incorrectly relied on its frozen
clock to keep the production shipping budget open during verification. It
still asserts that healthy WAL is stored before the first customer lookup and
that all three configuration deadlines and cleanup complete; the identical
restore assertion now runs after shutdown, under the test cancellation token.

## Ten reports already fixed on main

[PR #347](https://github.com/TerraScale/terrascale/pull/347), merged as
`aadc66a2521a9f754488eaa4d71e87de91212762`, contains the relevant fixes:

- #232, #236, #237, #240, #252: explicitly verify the real catalog before
  fixture setup. #232 also retries transient busy tap reads without releasing
  acknowledgements; #236 measures the snapshot stall with controlled time.
- #235, #238, #239, #251: verify the catalog in the backlog, multipart, and
  retention fixtures before creating management resources.
- #250: wait for the first public restore window before choosing a restore
  point, then recheck the whole-second boundary before deleting documents.
  The private CI bucket was restored separately; this task changes no secrets
  or bucket provisioning.

The old startup-503 reports are also explained by the expensive catalog scan
fixed in [PR #255](https://github.com/TerraScale/terrascale/pull/255).
The linked CI logs for runs `38485525906`, `38476089682`, `38683863835`, and
`39853260399` were read and retained in `/tmp/ts-backup-issue-audit/`.
Their reported failures precede #347.

#347 records 20 repetitions of each ACK, multipart, backlog and retention test
at `e663448d4355e9193e943dc581ab212f5d9aad23`, plus five real-Tigris restores
at `820835ee684b1959a9a361cbcd19b84cacc8b412`, with no failures or skips.
This audit independently verified all 20 ACK report checksums on mariogk-dev
(`~/remote-builds/ack-evidence-e663448d/verification.json`) and all five
Tigris log checksums on delta
(`~/.local/state/terrascale/backup-250/820835ee684b1959a9a361cbcd19b84cacc8b412/proof-summary.json`).
The multipart/backlog/retention evidence is recorded in #347 under beta's
`~/remote-builds/backup-slow-fixed20-e663448d4355e9193e943dc581ab212f5d9aad23/`;
beta was unreachable during this audit, so those artifacts were not rechecked.

## Scope and limits

Open PRs #423, #453 and #455 and the "Complete Cross-Region Replication" lane
were checked before editing; none owns this backup shutdown code. No workflow,
replication sender, API, storage format, recovery validation, or test assertion
was weakened. `StorageFormat.cs` is unchanged; no format bump is needed.
Full-suite contention is not proven by isolated repeats; CI remains the merge
gate, and unrelated Slow failures remain visible.
