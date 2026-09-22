# RustFS tiered segment qualification

- Endpoint: `http://127.0.0.1:19000/`
- Bucket: `terrabase-tiered-qualification`
- Records: `100,000`
- Payload bytes: `128`
- Published segments: `1`
- Compaction records: `100,000`
- Seed and compaction time: `8.358 s`
- Reopen/read/scan time after SSD cache deletion: `4.634 s`
- Scan count: `100,000`
- First value: `0`
- Last value: `99999`
- Total process interval: `13.450 s`

The segment manifest was committed only after the RustFS object upload, then
the local SSD copies were removed. Reopen therefore verified the read-through
cache refill and segment checksum validation against RustFS.