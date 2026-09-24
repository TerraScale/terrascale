# 40k × 8 KiB memory diagnostic stopped before receipt

| Observation | Result |
| --- | ---: |
| Workload | StorageEngine; per-key incompressible; 40,000 records; five samples configured |
| Cgroup | 256 MiB memory.max; memory.swap.max = 0 |
| Runtime before stop | 13m 32.860s wall; 14m 37.500s CPU |
| cgroup peak | 256 MiB |
| OOM / OOM kills | 0 / 0 |
| Local engine files at stop | 1.1 GiB |
| Completed sample lines | 0 |

The run used the post-compaction-insertion-fast-path Release binary. It was
stopped while `CompactAsync` repeatedly decompressed state-index blocks in
`ShedCurrentRecord`, whose applied-sequence lookup is redundant for records
selected into the current-only compaction cut. This is a performance diagnostic,
not a passing larger-than-memory qualification. The live cgroup snapshots,
managed stacks, and systemd journal remain alongside this note.
