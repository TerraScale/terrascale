# RustFS tiered segment qualification — ABORTED

| Measurement | Result |
| --- | --- |
| Status | **ABORTED** |
| Target | 1,100,000 records |
| Completed checkpoint | 100,000 records |
| Seed elapsed at checkpoint | 241.327 s |
| Source | commit `67e35f443565c54eb731d6ea2dd989cb5c87b7be`, dirty working tree |
| Build | Release, .NET 11.0.0-rc.1.26425.128 |
| Resource envelope | cgroup `MemoryMax=4,294,967,296`, `MemorySwapMax=0` |
| Final cgroup memory peak before stop | 451,637,248 B |
| Final cgroup events before stop | `oom=0`, `oom_kill=0`, `max=0` |
| Process peak RSS at checkpoint | 368,222,208 B |
| Local storage at checkpoint | 87,396,973 B |
| RustFS prefix | `terrabase-tiered/current-1100k-20260923T002052Z` |
| Confounding load | Normal-priority concurrent `VBCSCompiler` and multiple `dotnet build/test` processes; this is not a clean throughput result |
| Stop evidence | [`cgroup-final-before-stop.txt`](cgroup-final-before-stop.txt) |
| Progress | [`progress.jsonl`](progress.jsonl) |
| Console | [`tiered-rustfs-current-1100k-20260923T002052Z.console.log`](../tiered-rustfs-current-1100k-20260923T002052Z.console.log) |
| Partial receipt | [`tiered-rustfs-receipt.json`](tiered-rustfs-receipt.json) |

The process was terminated after the 100,000-record checkpoint. No completion,
compaction, reopen, or remote verification claim is valid for this run.
