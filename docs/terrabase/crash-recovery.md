# Process crash recovery

Database creation flushes the initialized database and wrapped key, publishes the key and syncs the directory hierarchy, then publishes the database and syncs its directory. Startup removes files with the explicit unpublished `.creating` identity. An interrupted creation is distinguished by that identity, never by a zero-length main file.

Deletion first flushes a `.deleting` marker and its directory, removes the database and syncs its directory, then removes its wrapped key, other sidecars and marker. It syncs those removals and any removed parent directories before completing. Startup completes deletion only when that marker proves deletion was requested. A missing main file without that marker leaves its key and log untouched.

A write intent left by an interrupted process triggers a database open, journal recovery and integrity check. A healthy copy whose incarnation is named by its intent is accepted. The intent writer retains its existing single canonical 26-character incarnation. The reader also understands a future source:replacement pair, requiring successful log replay and integrity checking before accepting either named incarnation. This release does not write that pair: a previous release rejects it while opening the catalog. Healthy rewrite ancestry is held only in process memory, with no predecessor recorded in the file. Consequently a crash after a rewrite rename can leave a replacement that the single-value intent cannot identify; that copy remains refused. A later release must introduce the paired writer after the reader has deployed across the rollback window. The rewrite path must not acquire a production caller before the paired intent writer ships; reviewers must verify that its only caller remains the crash harness.

Recovery has three outcomes:

| Outcome | Evidence | Action |
| --- | --- | --- |
| PASSED | Full integrity returns `ok` and identity matches a named incarnation | Retire intent and admit the copy |
| DAMAGED | Integrity reports corruption, identity is provably wrong, or the main file is proved empty or missing while its log remains | Persist the bounded fault reason before retiring the intent; refuse all protocols |
| INCOMPLETE | Busy/locked, I/O/storage failure, key unavailable, timeout, interruption/cancellation, constraint or any other inability to complete the proof | Keep intent, record no permanent fault, return retryable unavailable, retry in the background |

Proofs copy the main file and WAL into reserved scratch space, unwrap the existing key without rewriting it, and open only the scratch database. They never delete, truncate, recreate or checkpoint the source database, log or key. Opening an existing recovery snapshot also rejects an empty/missing main before opening; it never repairs it by deletion. Scratch copies are disposable. `terrabase.storage.faulted.databases` reports the current persisted fault count, including after restart; `terrabase.storage.proof.pending` reports pending databases and `terrabase.storage.proof.incomplete` counts incomplete attempts by bounded reason. Retries run sequentially at exponential delays of 2, 4, 8, 16 and at most 32 seconds; shutdown stops them and retains unresolved intents.

A fault is never cleared automatically, including after a later successful proof. An operator has the untouched original database files, log and wrapped key, plus the persistent fault record with its reason. Supported recovery today is restoring a backup into a new database; that backup may not contain the most recent acknowledged writes. Preserve the originals for investigation before deleting the faulted database.

Connections explicitly use WAL journaling. Point-in-time rewind commits with full synchronization. Change capture without a backup tap can discard completed transactions while respecting active shape, replica and rewrite retention floors. A sequential idle pass visits databases on disk every 30 seconds, including unopened databases after restart, and skips busy writers. Each database trims at most 16,384 rows and 64 MiB per visit (one oversized row may exceed the byte allowance); the retained-row gauge reports progress. A live backup tap or a backup/rewrite anchor prevents the idle pass from discarding its history.

The local crash probe runs the built service with generated credentials and isolated temporary data:

```sh
export PATH="$HOME/.local/bin:$HOME/.local/share/mise/shims:$PATH"
export DOTNET_ROOT="$HOME/.local/share/mise/dotnet-root" DOTNET_PROCESSOR_COUNT=4
nice -n 19 python3 e2e/env/engine-crash.py /path/to/main-build/terrabase.dll
```

It requires Linux, a C compiler, curl with HTTP/2, and the Release build of TerraBase. It kills a node after an acknowledged write, during key publication and during database deletion, then restarts the real service and checks the retained rows and file pairs. The filesystem probe pauses the service at a completed operating-system call before sending SIGKILL. This verifies process-crash behavior; it does not emulate a power failure.

The probe starts the previous build on an isolated copy of every directory left by a killed process before this build recovers it. The previous build may fault a tenant copy according to its existing policy; the assertion here is that the node starts without rejecting the intent value. The rewrite probes seed a stale canonical intent at the existing pre-writer checkpoint, after quiescence has drained normal operations, so successful retirement cannot erase the recovery case before the kill. The post-rename case explicitly verifies refusal with the retained single-value writer. The probe also measures three restarts with 20 databases of about 200 MB and four of about 1 GB, records replay separately from integrity work, and verifies that idle history drains after restart without tenant requests. All processes and temporary data are removed on exit.

Idle draining was observed after restart without tenant writes or opens: 60,000 retained rows fell through 43,616, 27,232, 10,848 and 1 across four 30-second passes. The final gauge was 2, including the healthy database's retained row.

## Recovery measurements

Each set of three local Linux x64 restarts used 20 main files of 213,561,344 bytes and four of 1,063,346,176 bytes (8.525 GB total), with 1 MiB BLOB rows and no secondary indexes on the payload table. Each sample committed another 16 MiB update per database with automatic WAL actions disabled, left all 24 intents open, and killed the process. WAL files totaled 0.714, 1.429 and 2.143 GB. Times include four logical processors at nice 19 on a shared machine. Before creating data, memory was 31 GiB total / 8 GiB available and the 1 TB temporary filesystem was 32% used; the harness reserves 24 GB of disk headroom below the 80% limit.

| Run | Recovery phase (s) | Launch to ready (s) | Open and replay (s) | Replay (s/WAL GB) | Integrity (s) | Integrity (s/main GB) |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Initial 1 | 25.861 | 30.192 | 1.117 | 1.563 | 22.871 | 2.683 |
| Initial 2 | 19.061 | 22.121 | 1.992 | 1.395 | 16.473 | 1.932 |
| Initial 3 | 23.474 | 26.945 | 2.885 | 1.346 | 20.222 | 2.372 |
| Rebased 1 | 18.094 | 20.784 | 1.049 | 1.468 | 16.703 | 1.959 |
| Rebased 2 | 16.955 | 19.826 | 1.642 | 1.149 | 14.986 | 1.758 |
| Rebased 3 | 16.805 | 19.552 | 2.233 | 1.042 | 14.253 | 1.672 |

The replay timer covers opening the isolated database copy and reading its incarnation, which forces journal recovery. The integrity timer covers the full integrity query. Key unwrapping, isolated file copying, queueing, fault-ledger commits and intent retirement remain in the recovery-phase total. Earlier table samples predate isolated-copy proofs; the crash harness emits current timings for this path. Cache eviction was requested with per-file DONTNEED on only the harness's database and WAL files after each measured kill; no global cache was dropped on this shared host, and fully cold device caches are not claimed.

Startup recovery stays sequential under the creation lock and publishes SYSTEM after one proof attempt per intent. Readiness waits for SYSTEM and the intent journal to open and bootstrap, fault counts and startup proofs to be processed, catalog initialization/verification, platform seed completion, and the normal writable-storage readiness probe. A clustered node with NATS and peers also waits for a known regional owner, a started home-push service and ready home leases. An INCOMPLETE tenant proof does not wait for its background retries before SYSTEM is published or readiness can succeed; that tenant alone stays unavailable. SYSTEM or intent-journal failure still prevents startup. For a planning envelope of 120 databases and 6 GB of main files, with per-database WAL depths covered by these samples, the measured projection is under 69 seconds: 6 × 2.684 s/GB integrity + 120 × 0.326 s maximum open/replay + 5 × 1.874 s ledger/key overhead + 4.331 s startup overhead. A threefold margin rounds to 210 seconds, below the 1,800-second readiness allowance. Only this measured envelope has that planning allowance: 120 databases, 6 GB and the sampled journal depths. There is no runtime size guard. The allowance must be revalidated against the isolated-copy timings; it is no guarantee for arbitrary file sizes, journal depths, storage speeds, index-heavy schemas or host contention. If a node cannot finish its startup proofs within the release system’s 1,800-second start budget, the release system rolls it back. The operator preserves the data, inspects startup proof timings, pending/fault metrics and disk/key availability, remedies the unavailable resource or restores affected databases from backup into new databases, and retries the release once startup fits the budget. Do not delete evidence or clear a fault to force readiness.

The rollback build was main commit `d91c5b9660f6e862363eeabae5d08f39d6f9dfee`. It started successfully on all nine isolated crash-directory copies in each full run, including both rewrite kill points, creation, deletion and all three large-file samples. The main build's existing policy can fault copies after an unclean exit; rollback proof here concerns node startup and readable intent values. A stale single-value intent after a rewrite rename remains the explicitly deferred writer-release case.

The multi-region fault shard runs this harness as a required step before starting its four-node stack. It reuses the PR service artifact, builds the PR base revision separately for rollback checks, and removes the isolated baseline source and all crash data before continuing the fault journey.
