# RustFS tiered segment qualification

- Endpoint: `http://127.0.0.1:19000/`
- Bucket: `terrabase-tiered-qualification`
- Records: `10,000`
- Payload bytes: `128`
- Published segments: `1`
- Compaction records: `10,000`
- Seed and compaction time: `1.551 s`
- Reopen/read/scan time after SSD cache deletion: `0.904 s`
- Scan count: `10,000`
- First value: `0`
- Last value: `9999`
- Total process interval: `2.971 s`

The segment manifest was committed only after the RustFS object upload, then
the local SSD copies were removed. Reopen therefore verified the read-through
cache refill and segment checksum validation against RustFS.