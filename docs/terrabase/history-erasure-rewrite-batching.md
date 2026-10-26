# History-erasure rewrite cadence

Implements [decision 0017, complement (c)](decisions/0017-healthy-rewrite-off-the-writer.md).
All erasure phases share a ten-minute minimum between rewrite starts for each file: the ts-auth database and SYSTEM.
The first attempt can start immediately. Refusals consume the interval too; their existing exponential backoff can require
a longer wait. This reduces rewrite frequency; bounding each writer hold remains decision 0017's separate work.

Each phase still checks coverage or absence of residue under the writer and batches the ready records of its bounded page.
Only a successful rewrite of that exact source incarnation at or after the checked offset earns a receipt. Waiting writes
no receipt. Priority rotates after a successful auth rewrite so new final cuts cannot monopolize windows ahead of residue
and retirement. The two-entry schedule is process-local and monotonic; restarting resets it. There is no evidence of a
restart storm in the reported incident, so no persistent scheduling state or storage format change is introduced.

[The erasure contract](history-erasure.md) keeps its original deadlines: `activeOverdue` and `active_overdue` at request +
30 days for unfinished active phases, `backupOverdue` and `backup_overdue` at request + longest retention + 30 days until
certification. Retries and batching never extend these dates or hide overdue work. The true oldest-request age and its
day-20 alert, and the independent epoch-stall alert within 15 minutes, are unchanged.

A ready page/phase may wait for the next ten-minute window; competing phases and pages need further windows. Existing
seven-day feed retention, fence retirement after day 15, and fourteen-day bucket-snapshot retention still govern progress.
These waits fit within the existing thirty-day allowances in the healthy workflow; the Fast regression drives three
staggered erasures through real rewrites, backup certification before their backup deadlines, and retirement. Offline
holders, epoch recovery and failed rewrites can already exceed deadlines and continue to report overdue; batching adds
no deadline exemption and makes no new unconditional completion-time promise.
