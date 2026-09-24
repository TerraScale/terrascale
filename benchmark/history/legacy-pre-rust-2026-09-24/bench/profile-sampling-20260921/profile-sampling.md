# Index-profile sampling comparison

Documents: 4096; sample limit: 256; trials per strategy: 9; warmup rounds: 2; snapshot capture: 93.004 ms.

Each strategy receives the same immutable snapshot and sample limit. IDs encode two field populations: the first half has `early`, the second `later`. Selection receives no queries or workload observations. Trials alternate strategy order; filesystem/JIT state is warm.

| Sampling method | Median profile + candidates ms | p95 ms | Median allocated bytes | Sampled documents | Candidate paths | Later-field observations |
| --- | ---: | ---: | ---: | ---: | --- | ---: |
| OrdinalPrefixV1 | 5.487 | 12.095 | 868992 | 256 | early | 0 |
| OrdinalStratifiedV1 | 6.723 | 7.220 | 626416 | 256 | early, later | 128 |

Timing includes profile aggregation and deterministic candidate generation, excluding ingestion and snapshot capture (reported separately). Allocations are process-wide deltas and exclude the snapshot's retained metadata/body artifact. They are not peak-memory measurements. Raw trials and the subject assembly digest are in `profile-sampling-receipt.json`; command, repository state, runtime and host are in `run-receipt.json`.

This constructed dataset checks ordinal-prefix bias under equal sample limits. It does not establish future query benefit, population-wide type guarantees, Jev benefit, SQLite superiority, or larger-than-memory behavior. Snapshot capture still sorts and retains collection-wide key metadata.
