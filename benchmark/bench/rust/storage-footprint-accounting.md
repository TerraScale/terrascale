# Storage footprint accounting correction

The benchmark driver previously labelled the difference in directory file
lengths as bytes written. SQLite checkpoints and WAL reuse invalidate that
interpretation. A full workspace test observed 4,096 bytes of file growth
during 16 document reads and failed the asserted zero-write count.

| Measurement | Previous interpretation | Current interpretation |
| --- | --- | --- |
| Sum of database, WAL, and sidecar lengths | Durable bytes written | Storage footprint at the sampling instant |
| Difference between samples | Bytes appended, clamped at zero | Signed footprint change, including shrinkage |
| Publication sequence difference | Not reported by the workload counter | Document revisions committed during the window |
| Equal footprint changes with and without indexes | Zero write overhead qualified | Write I/O overhead unqualified |

The workload checks require one publication per committed document write and
no publications from reads or queries. Optimizer checks inspect descriptor
state and publication counts. The footprint diagnostic reports its samples
without awarding a write-overhead verdict. Actual write I/O qualification
still needs a measured I/O counter and controlled comparisons under the
required resource ceiling. These local checks do not establish that result.
