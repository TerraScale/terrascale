# Healthy rewrite execution (decision 0017, PR 1)

The copy reads one source transaction beside normal writes, without the store writer or engine lifecycle lock.
Catch-up reuses backup's encoder and restore's replay, preserving rowids and schema names. In a converged source
transaction, the source and replayed scratch are logically digested through the same commit. The digest checks schema,
rowids, values and commit offset; it excludes engine tables, the renewed incarnation, derived shape rows and the
node-local tables listed below. That same list drives the final refresh. Digests check cancellation per schema/row.

A full digest runs only before the lifecycle lock. A bounded top-up absorbs writes made during it; subsequent top-ups
run after lifecycle/reader waits and after the tenant operations wait. Each off-writer stage gets at most eight rounds.
Convergence requires at most 1,024 changes, 4 MiB of encoded replay and 250 ms. Bulk copy and accumulated scratch changes
are checkpointed/fsynced off the writer, followed by a small replay, so the final scratch fsync does not inherit the full
copy or catch-up history. The top-up after tenant operations become idle may briefly hold their gate while syncing.

The total pre-writer deadline is `min(120 s, 15 s + source bytes / (4 MiB/s))`, including capture setup, lifecycle,
reader and operations waits. The measured 54 MB baseline completed in 1.1 s; the slower allowance leaves substantial
margin without pinning WAL/CDC, retention, GC and the frontier indefinitely. Disk workspace is reserved through
`DiskBudget`; 72 MiB of bulk working memory is reserved before copying. Three consecutive 100 ms samples abort for
a queue at least as deep as the I/O worker count (at least two), other admissions consuming 90% of capacity, or inability
to reserve a 4 KiB memory probe. The initial bulk scope leaves a foreground I/O worker available. Cancellation and host
shutdown unwind readers, scratch, retention and admission; internal deadline/pressure expiry is a retryable refusal.

Quiescing first stops new backup taps, but leaves existing taps' lineage intact through reader/operations waits and
preparation. Refusals there do not restart a backup generation. After those checks/top-ups, lineage moves so existing tap
owners close their readers. The operations gate is released during that close wait so a tap operation already waiting
on it can finish; it is acquired again, followed by another top-up, before the final writer acquisition. A tap records lineage before checking quiescing, closing
the tap-open race. The backup-owner close wait necessarily follows the bump; refusal/cancellation in that wait or later
can still restart a generation. The rewrite reuses one I/O admission across the lifecycle lock.

After backup taps close and before taking the tenant operations gate again, the source's historical WAL is checkpointed on Turso's sole writer connection under the separate
`a rewrite's log checkpoint` holder, and its file is fsynced off the writer. Turso does not support a concurrent second
checkpointer. Checkpointing before the tap drain can wait on a pinned backup snapshot, so that ordering is forbidden.
The final cut refuses a source WAL over 16 MiB before checkpointing it: source work cannot inherit an
arbitrarily large historical backlog. Checkpointing/fsyncing the old file remains necessary before removing its WAL:
a crash before the rename must still find the complete old lineage. The existing crash order is unchanged.

The final 750 ms timer starts at writer acquisition and includes its drain (at most 250 ms), replay, refreshes, checkpoints
and fsyncs; it is checked again before publication and before rename. Preflight reads at most 1,025 CDC rows and derives
its end commit from that read: no unbounded commit-marker scan. The final delta is at most 1,024 rows and 4 MiB, with no
schema changes. Refreshed table hashes and the final commit offset are compared under the writer. Rename and both directory
fsyncs retain their existing ordering and uncertain-rename fail-closed handling. Native disk calls already running cannot
be interrupted (`TursoCommand.Cancel` is a no-op), so the deadline bounds cooperative work, not arbitrary device stalls.

`terrabase.storage.rewrite_step{step,database=system|tenant}` records copy/digest, replay/preflight, source/scratch
checkpoints and fsyncs, node-local refresh/hash, rename and both directory syncs. `terrabase.storage.rewrite_refused{reason}`
uses the bounded Safety/Budget/Deadline/Pressure reasons. The Slow rolling-restart regression retries only Budget refusals,
at most five times, logs every refusal and still requires success; other refusal reasons fail immediately.

The rewrite cursor holds CDC retention back. Its uncaptured-commit baseline is read under the initial writer barrier:
an already-running retention/GC transaction commits before that read and is included in the copied snapshot. Reading the
baseline before this barrier falsely refused SYSTEM rewrites under replicated catalog load, even for a no-op retention
pass. The five-second pass is the counted SYSTEM writer in `CatalogGateRollingRestartTests`; ordinary organization
creates do not write SYSTEM's rebuild ledger. Most frequent `a rebuild repair` holds are record/retire transactions on the
separate intent journal, which is not rewritten.

Until decision 0017's capture-always-on PR, routine uncaptured writers are handled as follows:

| Writer | Tables changed | During a rewrite |
| --- | --- | --- |
| Five-second CDC retention and GC | `turso_cdc`, `_tb_peers`, `_tb_state`, `_tb_merkle`, `_tb_gc_floor` | Defer under the writer; a pass already running finishes before the baseline. |
| Hourly feed retention | `_tb_feed`, `_tb_feed_floor` | Defer under the writer, returning no work so the pass ends. |
| Peer acknowledgement and stable report | `_tb_peers`, CDC retention | Continue; refresh peer rows at the final cut, then reset acknowledged IDs. |
| SYSTEM fault/rebuild/recovery/delete ledger | `_tb_rebuild` | Continue; refresh the table at the final cut, preserving inserts, updates and deletions. |
| Shape sealing, snapshots, retention and lineage | `_tb_shape_chunk`, `_tb_shape_snapshot` | Continue; both derived tables belong to the old incarnation and are cleared at the final cut, as TursoCopy already does. Frontier advancement still defers. |
| Operation-intent record/retire | `_tb_write_intent` in the separate intent journal | Continue on that separate store; it is never a rewrite source. |

Final refreshes run with capture off and the source quiesced. Each of `_tb_peers` and `_tb_rebuild` is limited to 1,024
rows and 1 MiB, on both source and scratch before deleting or copying. Peer rows contain integer progress and an
incarnation; rebuild rows contain three identity keys, incarnation/proof fields and length-bounded reason/source text,
with one row per database. Their total row counts can grow, so exceeding either hard budget refuses safely instead of
performing unbounded work under the writer. Unknown/default capture suspensions still invalidate an overlapping rewrite,
including uncertain commits; refusal messages identify the suspending method. Mixing an exempt suspension with a counted
one in the same transaction still counts.

The initial snapshot must include the requested erasure offset. Old CDC history is never copied, and catch-up only replays changes
after that snapshot. Refreshes read only node-local progress/refusal tables, never erased documents or old log images;
derived shapes stay empty. TursoCopy creates the new incarnation before catch-up; identity resets stay uncaptured. The
receipt offset covers the final catch-up. Refusal deletes scratch and yields no receipt; an uncertain rename still fails
closed. The lifecycle lock precedes final quiescence to match deletion/rebuild lock order; its wait is capped at 30 s.
Only a refusal after the late lineage move may restart a backup generation. No HTTP, storage format, encryption key or receipt format changes.

The Fast concurrency regression starts a real uncaptured SYSTEM retention transaction before the rewrite's writer barrier,
then commits catalog creates and a real rebuild-ledger fault record after the snapshot while the final cut waits for its
reader. More than 1,024 captured changes are also committed during that wait, forcing the extra off-writer catch-up.
It checks those documents, catalog documents, peer progress and the durable ledger reason through the replaced store. The existing
roughly 50 MiB SYSTEM/tenant tests retain their real-clock writer-hold limit of one second and every-write digest checks.

Round-3 regressions cover tenant updates/deletes and collection create/drop during copy, real peer applies during copy,
a deliberately misclassified uncaptured write caught by the replay digest, eight nonconverging rounds, final row/byte/DDL
budgets, the final deadline, the total deadline and sustained I/O saturation, cancellation and shutdown mid-copy,
one-admission capacity, and a real child-process crash in catch-up followed by startup scratch cleanup. Backup reads during
a pinned rewrite exercise the bounded CDC trim from #720 and must retain the rewrite anchor. Each test owns its host,
files and hooks and runs at default parallelism.

Known limits: old peer epochs and rebuild rows can accumulate beyond the refresh caps (review finding 7), causing safe
but persistent refusal until that separate retention problem is addressed. Full digests verify a converged snapshot;
later bounded top-ups/final replay rely on the tested replay implementation, not another full scan under the writer.
The synthetic I/O saturation test exercises the 0013f queue feedback loop, not an arbitrarily stalled fsync syscall.
The separate source checkpoint still serializes with Turso writers; its duration is exposed separately and a storage
device that stops completing I/O can exceed the measured hold target. General capture-always-on remains decision 0017 PR 2.


The combined #707 includes the replay refactor originally reviewed in #706. Its final validation on main `e5d70309c`
used one sequential `ts-heavy-any` job, native download first, .NET 11 Release, default TUnit parallelism and CI's pinned
SDK dependency. Both suite builds had zero warnings/errors. Fast selections passed 375/375 (92 rewrite/erasure/hold/group
commit, 11 retention/copy, 166 rebuild, 106 recovery/shutdown); the Slow rolling-restart test passed three times, covering
60 restarts and 30 completed SYSTEM rewrites. Two Budget refusals each succeeded on attempt two. Real-clock 54,013,692-byte
SYSTEM and 53,951,716-byte tenant sources held the healthy-rewrite writer 65/70 ms with 668/173 concurrent commits retained.

Step maxima across the two large-file cases and the rolling-restart attempts:

| Step | Maximum (ms) |
| --- | ---: |
| Final replay | 119.494 |
| Final source checkpoint | 12.934 |
| Peer refresh | 6.513 |
| Rebuild-ledger refresh | 0.562 |
| Final scratch checkpoint | 9.165 |
| Final source fsync | 2.017 |
| Final scratch fsync | 4.852 |
| Rename | 3.779 |
| Directory fsync before rename | 3.284 |
| Directory fsync after rename | 2.524 |
| Historical source checkpoint (separate hold) | 20.264 |
| Historical source fsync (off writer) | 5.319 |

The complete final pass peaked at 156.465 ms. The instrumented unchanged `28a5b6b13` replicated baseline passed once;
its maxima were 9.476 ms final replay, 23.493 ms source checkpoint, 5.750/1.310 ms peer/ledger refresh, 1.583 ms scratch
checkpoint, 0.070/2.401 ms source/scratch fsync, 1.218 ms rename and 2.556/1.269 ms directory fsync. Loads differ between
samples. The original CI 750 ms expiry did not reproduce; no claim is made about its precise stalled step.

An unmodified-main recovery/shutdown control at `ece4231f2` passed 104 and failed two absent-directory delete cases:
the deliberately armed lifecycle fault escaped through background convergence during shutdown. The first host now uses
a scoped fake clock while asserting its crash state, as neighboring interruption tests do; restarts still use real time.
Production recovery/shutdown code is unchanged. The final 106-test selection passes, including both reported shutdown
variants. Full Fast is left to CI and full Slow was not run.
