# Diagnostic run stopped before final verification

This 1,100,000-record RustFS run was deliberately stopped at 2026-09-23 06:58 UTC (process exit 143). The detailed receipt still says `running` because the process was terminated during its final cold reopen. It is **not** a passing qualification.

| Phase | Observed result |
| --- | ---: |
| Synced WAL seed | 1,100,000 records, 117.604 s |
| First compaction and S3 publication | 202.945 s |
| First cold reopen and exact scan | 1,100,000 records, 194.732 s |
| Updates | 1,000 records, 391.326 s |
| Deletes | 1,000 records, 34.916 s |
| Second compaction and S3 publication | 204.831 s |
| Final cold reopen | Stopped after over 20 minutes; final exact scan and deleted-key checks were not reached |
| Peak cgroup memory / swap / OOM kills | 1,652,510,720 B / 0 B / 0 |
| Cgroup writes at last sample | 20,039,669,248 B |

The frozen benchmark assembly is SHA-256 `d1035d608aa7ff7a608d13dc4de64216cb8f27563c6656204a499129293942ca`. Live stacks during the final reopen showed LZ4 disk-map lookups under `RefreshStateCoreUnsafe`. After an updated head record raised the replay high-water sequence, later unique records were unnecessarily probed for duplication. A bounded recovery sequence membership fix was prepared in the source tree after this binary was frozen. The run also overlapped unrelated compilation on the shared host, so its wall times are diagnostic, not controlled throughput measurements.

The retained `tiered-rustfs-receipt.json`, `progress.jsonl`, `resource-samples.jsonl`, and `binary-sha256.txt` are partial evidence only. A fresh complete run is required.
