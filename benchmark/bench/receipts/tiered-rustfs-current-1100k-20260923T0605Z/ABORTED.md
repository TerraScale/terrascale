# Aborted RustFS 1.1M diagnostic

| Field | Observed result |
| --- | --- |
| Status | **Aborted; not a successful qualification** |
| Target | 1,100,000 records |
| Payload | 128-byte repeated `x` payload (`repeated-x`) |
| Mutations | None; updates and deletes were not configured |
| Cgroup | `memory.max=4,294,967,296` bytes; `memory.swap.max=0` |
| Cgroup memory peak | 981,803,008 bytes |
| Cgroup events | `low=0`, `high=0`, `max=0`, `oom=0`, `oom_kill=0` |
| RustFS objects after initial publication | 4 objects, 461,989,086 bytes |
| Benchmark assembly SHA-256 | `04dd552ede16197ab72217c267d23470407f629c1aa040797f2f5109c975903a` |
| Subject | commit `67e35f443565c54eb731d6ea2dd989cb5c87b7be`, tree `69203400f13c7e311cf5fb569a081eeab36e8cbc`, dirty worktree digest `fc22ab08a05770ceb6bf2802133b5d49e292ce3d8ba9f15a4f8f756c15996562` |

The process was stopped during final point-read verification after the final
scan reached its 1,100,000-record checkpoint. The durable receipt therefore
remains `Status=running` and has no completed final phase or success receipt.
This file records the abort and must be read with the raw receipt and progress
stream; it does not convert the interrupted run into a passing result.

## Verified phases before the stop

| Phase | Evidence |
| --- | --- |
| Seed | 1,100,000 records; 112.789 s in the committed phase metric. The final progress checkpoint reached 1,100,000 at 113.278 s elapsed. |
| Initial compaction and publication | 203.813 s; 4 RustFS objects totaling 461,989,086 bytes. |
| Initial remote reopen and exact scan | 1,100,000 records and 512 point reads; 188.035 s in the committed phase metric. |
| Final scan | Progress reached the 1,100,000-record checkpoint at 663.797 s elapsed. No final phase metric was committed. |
| Final point reads | Started, but completion was not recorded. |

Observed local engine storage samples were 371,790,941 bytes at the end of
seed, 563,162,774 bytes during the initial scan, and 237,573,884 bytes during
the interrupted final point-read phase. The interrupted receipt's aggregate
peak-local field is not used as a completed-run measurement. The resource
envelope captured 3,678,208 read bytes and 5,305,819,648 write bytes; later
resource samples reached 7,117,118,976 cumulative write bytes before the
stop.

The run shared the host with a concurrent API test suite. Its timings are
diagnostic observations only and must not be presented as clean throughput.
It does not qualify 100 million records, final point-read correctness, or a
complete 1.1M workload.

Evidence: [receipt](tiered-rustfs-receipt.json), [progress](progress.jsonl),
[resource envelope](resource-envelope.json), and [resource samples](resource-samples.jsonl).
