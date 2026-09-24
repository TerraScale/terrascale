# Frozen 10M RustFS seed diagnostic — stopped at 3.5M

This run was deliberately terminated at 08:57 UTC on 2026-09-23 after the
3,500,000-record seed checkpoint. It did **not** complete seeding, compaction,
remote publication, cold reopen, updates, deletes, or final verification. It
is a performance diagnostic, not a 10M qualification receipt.

| Completed seed interval | Elapsed (s) | Records/s |
| --- | ---: | ---: |
| 0–0.5M | 42.833 | 11,673 |
| 0.5–1M | 46.803 | 10,683 |
| 1–1.5M | 72.907 | 6,858 |
| 1.5–2M | 109.684 | 4,559 |
| 2–2.5M | 164.671 | 3,036 |
| 2.5–3M | 246.272 | 2,030 |
| 3–3.5M | 340.481 | 1,469 |

| Resource at the 3.5M checkpoint | Observed |
| --- | ---: |
| Local engine bytes | 2,422,449,024 |
| Cgroup memory peak | 2,224,427,008 |
| Cgroup device writes | 6,695,303,168 |
| Swap / OOM kills | 0 / 0 |
| Open file descriptors near stop | 1,439 |

The isolated scope had a 4 GiB memory limit and zero swap. The frozen Release
benchmark assembly was SHA-256
`8845dff9857eb7ded0521462aa7e28c79dcfe41061f46ae69b4bd6111c91e0ae`.
The worsening seed rate makes this binary unsuitable for the requested 100M
scale. Active host builds and tests also competed for resources, so these are
diagnostic wall times, not clean throughput measurements. Source work after
this frozen assembly introduced proven-new document state-map insertion and a
document-key high-water shortcut; a new-source run is required to measure
their effect.

`progress.jsonl`, `resource-samples.jsonl`, and
`diagnostic-stop-envelope.json` preserve the exact checkpoint and cgroup
measurements. The workload's generated `tiered-rustfs-receipt.json` still
shows its last running state because the process was terminated externally;
it must not be interpreted as a pass.
