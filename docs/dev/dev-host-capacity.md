# Dev host capacity

`ts-ship` Fast batches and `ts-heavy-any` jobs share two dev hosts, mariogk-dev
and beta. They wait for capacity rather than overloading a host. Tests keep
their own parallelism; admission only decides how many suites run on a host at
once.

## Limits

Every job runs as user `mariogk`, inside `user-1000.slice`. The HomeLab fleet
module `fleet/nix/modules/roles/dev-vm/capacity.nix` sets that slice's limits,
and systemd-oomd kills its cgroups when memory pressure stays above 70% for
10 seconds.

| Host | Cores | RAM | TasksMax | MemoryHigh / Max | `ulimit -u` |
| --- | --- | --- | --- | --- | --- |
| mariogk-dev | 46 | 31 GiB | 8192 | 22 / 26 GiB | 128091 |
| beta | 12 | 14 GiB | 8192 | 10 / 12 GiB | 58653 |

`ulimit -u` is never the limit, even when Go prints `newosproc ... (ulimit -u)`
or .NET reports `pthread_create` EAGAIN. The pids controller rejects those
forks:

- On 2026-11-03, mariogk-dev's slice showed `pids.peak` 8193 and
  `pids.events max 1123`. The kernel logged `fork rejected by pids controller`,
  and the ship run that failed with `newosproc` ran at the same time.
- Memory pressure throttled both slices millions of times (`memory.events high`:
  3.2M on mariogk-dev, 5.0M on beta). In three days, oomd killed whole session
  scopes, and with them agent sessions.

Peaks for one suite, measured in their own scope:

| Suite | Threads | Anonymous memory | Average cores | Time |
| --- | --- | --- | --- | --- |
| `dashboard-test-fast` (mariogk-dev) | 1711 | 4.2 GiB | 9.0 | 166 s |
| `auth-test-fast` (mariogk-dev) | 428 | 2.6 GiB | 2.2 | 211 s |
| `terrabase-test-fast` (mariogk-dev) | 984 | 3.2 GiB | 7.2 | 83 s |
| `auth-test-slow` (beta) | 446 | 9.3 GiB | | |

The limits are different on each host. Threads run out first on mariogk-dev,
where several suites and the agents' browsers and editors share 8192 tasks.
Memory runs out first on beta, where one auth Slow suite fills its 10 GiB
MemoryHigh. Cancelled runs also left TerraBase test servers running for more
than 14 hours, outside any job.

## Admission

`~/.local/bin/ts-slot` is installed on delta, mariogk-dev and beta. Inside
`ts-heavy`, it runs every job, including each ts-ship batch:

- Each job runs in its own systemd user scope, `ts-job-<ns>-<id>.scope`. The
  scope measures the job's threads, memory and CPU exactly. When the job ends,
  is superseded or loses its owner, ts-slot kills every remaining process in
  the scope, including setsid'd and reparented ones.
- The budget is 85% of the slice's `pids.max`, 80% of `memory.high`, and the
  core count. Other usage reduces it: slice threads and memory outside job
  scopes, and load1 minus the running jobs' CPU.
- A job starts when its estimate fits the budget left after the running jobs.
  Each running job counts at its estimate or its current usage, whichever is
  higher. The estimate is the peak of the label's last five successful runs on
  that host, stored in `~/.local/state/ts-slot/history.json`. Labels with no
  history use the measured defaults built into ts-slot.
- Jobs wait in FIFO order. A later job may start ahead of older jobs that do not
  fit. Once the oldest has waited 120 seconds, only it may start. It starts on
  an idle host even if it is larger than the budget.
- A waiting job prints this line to stderr every minute, and ts-ship relays it:

  ```text
  ts-slot: waiting for slot on mariogk-dev: auth-test-slow needs threads=3000 mem=7.8G cpu=14; free threads=2312 mem=4.7G cpu=28; 2 running, queue 1/1, waited 0s
  ```

- A job holds nothing while it waits, and nothing after it exits. A file lock
  tracks each running or waiting job. When that job dies, the lock is freed.
  Any admission check or `ts-slot status` then reaps the job's scope.

`ts-heavy-any` and `ts-ship` pick hosts with `ts-slot status --json`. They
prefer hosts where the job fits now and nobody is waiting. Next come shorter
queues, then more headroom, then lower load. ts-ship places each batch on a
host and labels it with its make targets, such as
`auth-test-fast+dashboard-test-fast`. That host's ts-slot then queues the batch
until it fits.

## Commands

```sh
ts-host -v --label dashboard-test-slow   # every host's free capacity and fit
ssh beta '~/.local/bin/ts-slot status'   # budgets, running jobs and queue on one host
ts-slot reap                             # kill scopes whose owner is gone
ts-heavy-self-check --peer mariogk-dev   # supersession, FIFO waits and reaping
```

## Raising the task limit

ts-slot keeps jobs inside the current limits. Raising the limits fits more
suites at once. `TasksMax` is set on both `user.slice` and `user-1000.slice`, so changing it
needs root. Change `userTasks` in the HomeLab `fleet/nix/modules/roles/dev-vm/lib.nix`
profiles from 8192 to 32768 for mariogk-dev and 16384 for beta, then deploy the
fleet. To apply it immediately until the next deploy:

```sh
ssh mariogk-dev bash -c "'for s in user.slice user-1000.slice; do sudo systemctl set-property --runtime \$s TasksMax=32768; done'"
ssh beta bash -c "'for s in user.slice user-1000.slice; do sudo systemctl set-property --runtime \$s TasksMax=16384; done'"
```

On beta, memory remains the limit: one auth Slow suite fills it.
