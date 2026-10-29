# W0-4 multi-region lag lab

Standalone Linux process integration benchmark using real Turso files. All traffic stays inside three newly created network namespaces. Veth links form a triangle; **only namespace interfaces** receive netem. Root links and qdiscs are recorded before and after, and compared. The runner removes only its own processes and namespaces, retaining source, database files and results in its owned scratch directory.

Requirements: passwordless sudo on an authorized development host, Linux network namespaces, `ip`, `tc`, `ping`, `chattr`, Python 3.14+, Node 24, npm, and Rust **1.99.1**. Measured versions and hashes are in the evidence. Native source/home Btrfs database directories use `chattr +C` before file creation; the official replica inherits the scratch directory filesystem settings. No host network interface or canonical checkout is changed.

Turso source is pinned to `5c168be5ef6a32ac966daf5983502a4f694b37ad` (0.8.2); downloaded tar SHA-256 is `7ea83d5e28577cf9d8655b77705902379e06b82fb29424f6ea328b5f26f17ecb`. `prepare.py` verifies the cached archive on every run and regenerates the source tree and discards previous compiled artifacts. It retains the original MIT license, and appends the spike's C ABI to the unmodified upstream `cli/sync_server.rs`. The archived Cargo lock and npm lock pin every dependency, including sync's Linux x64 native client 0.8.2 and common packages 0.8.3. `build.sh` requires the explicit Rust 1.99.1 compiler and Cargo binaries and uses locked Cargo and npm restores. The upstream server is a prototype, not production code.

From the task checkout, with `feat/lag-lab` already published for the admission wrapper:

```bash
LAB_HOST=$(ssh delta ts-host -v)
LAB_SCRATCH=/home/mariogk/lag-lab-w0-4-repro/benchmark/lag-lab
ssh "$LAB_HOST" mkdir -p "$LAB_SCRATCH"
scp benchmark/lag-lab/* "$LAB_HOST:$LAB_SCRATCH/"
ssh delta env TS_HEAVY_HOST="$LAB_HOST" ts-heavy-any nice -n 19 bash "$LAB_SCRATCH/build.sh" --repo terrascale --branch feat/lag-lab
LAB_PYTHON=$(ssh "$LAB_HOST" "python3 -c 'import sys; print(sys.executable)'")
LAB_NODE=$(ssh "$LAB_HOST" "node -p 'process.execPath'")
LAB_TOOL_PATH="$(dirname "$LAB_PYTHON"):$(dirname "$LAB_NODE"):/run/current-system/sw/bin:/usr/bin:/bin"
ssh delta env TS_HEAVY_HOST="$LAB_HOST" ts-heavy-any sudo -n env PATH="$LAB_TOOL_PATH" nice -n 19 "$LAB_PYTHON" "$LAB_SCRATCH/lab.py" run 10 normal --repo terrascale --branch feat/lag-lab
ssh delta env TS_HEAVY_HOST="$LAB_HOST" ts-heavy-any sudo -n env PATH="$LAB_TOOL_PATH" nice -n 19 "$LAB_PYTHON" "$LAB_SCRATCH/lab.py" run 5 --repo terrascale --branch feat/lag-lab
```

Resolve the user's actual Python and Node binaries before sudo; root's mise shims are not configured on every host. Reselect with `ts-host -v` before each heavy invocation; if the least-loaded host changes, stage the scratch inputs there and rebuild through the wrapper. Do not run simultaneous jobs under this branch: admission supersedes the previous job. `run 2 smoke normal` runs one repeat, 70 ms only; smoke data is not published as the full matrix.

`normal` selects WAL/synchronous=NORMAL; omitting it selects WAL/synchronous=FULL. Both make a distinct implicit SQL transaction for **each row** at the writer and, for push/combined, at home. Transport batches up to 64 after-images without merging their SQL commits. NORMAL applies to source/home SQL connections, while the official replica uses its pinned defaults. NORMAL does not fsync each commit and cannot substantiate durable-ack performance; FULL is retained to expose that limit. Every profile runs 70 and 90 ms one-way, 100 and 1,000 offered commits/s, three repeats, and all four paths:

- `page`: official JS sync client over HTTP long-poll; native upstream protobuf page responses are passed unchanged. No JSON replacement for replication.
- `push`: a persistent TCP after-image stream, applied as real SQL commits at home.
- `direct`: the same stream added to a peer's provisional in-memory overlay, with no home hop.
- `combined`: writer → home SQL apply → a third namespace's official page-pull replica; lag is measured end to end.

Streams connect before the workload. Two seconds of paced warm-up transactions have negative sequences; measured rows have sequences 1..N. ULID keys and 256-byte values are synthetic. After-images are the deterministic inserted row values, verified against the final source SQL contents; this does not implement CDC capture or a durable outbox. One independent producer assigns absolute monotonic deadlines, a bounded queue feeds the SQL writer, and another bounded queue feeds the TCP sender. No per-commit request/ack RTT gates the producer. Overload retains every deadline and drains all rows; scheduled-to-observed latency exposes coordinated omission that commit-to-observed latency alone would hide. Queue high-water marks, full-queue encounters, enqueue time and every send batch are retained. Destination socket reads naturally backpressure on SQL apply; kernel qdisc statistics expose drops/backlog.

Each run prints its retained directory. Copy its `raw.json.gz` using `ssh HOST sudo -n cat PATH > FILE`; no database files need to enter Git. Python `CLOCK_MONOTONIC` and Node `process.hrtime.bigint()` share the same host clock, checked by bracketing a Node clock read with Python reads. No wall clocks are subtracted.

Raw archives also embed the measured source files as base64, checked against their recorded SHA-256 hashes. NORMAL used the harness immediately before a cleanup-only change: the final version tracks successfully created namespaces before deleting them. Its workload code is identical. FULL uses the final version.

Raw case schemas:

- `source.samples`: `[sequence, offered_deadline_ns, sql_begin_ns, local_commit_return_ns, enqueue_return_ns]`.
- `destination.samples`: `[sequence, batch_parsed_ns, destination_commit_return_ns]`; direct uses the parsed timestamp for overlay visibility.
- `pull.samples`: `[sequence, query_observed_ns]`, after actual page application and a local SQL query.
- `source.batches`: `[first_sequence, last_sequence, send_begin_ns, send_return_ns, rows]`.
- Each block has `warmup_samples`, full SQL/overlay row count and ordered-content SHA-256. Pull also records `[request_begin_ns, response_body_complete_ns, bytes, is_page_pull]`.

The runner verifies every sequence and full content against deterministic expected rows, not just between replicas. The auditor rechecks counts, hashes, monotonic timing, settings, CPU priority, clock alignment, no qdisc drops and cleanup, and regenerates the report table plus per-repeat JSON:

```bash
nice -n 19 python3 benchmark/lag-lab/summarize.py benchmark/bench/2026-10-29-lag-lab-full.json.gz benchmark/bench/2026-10-29-lag-lab-normal.json.gz
```

Percentiles use nearest rank over **all** measured commits, without removing outliers. Achieved source rate is N divided by the interval from the first measured deadline to the final commit, plus one offered interval. The summary also retains source/destination rates across the first-to-last observed commit span. Scheduled p99 starts at the offered deadline; lag p50/p99 starts at local SQL commit return. No sum of independently measured hop percentiles is presented as end-to-end latency.
