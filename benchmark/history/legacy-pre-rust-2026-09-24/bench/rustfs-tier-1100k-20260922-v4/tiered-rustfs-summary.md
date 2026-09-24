# RustFS tiered segment qualification

| Measurement | Result |
| --- | --- |
| Endpoint | `http://127.0.0.1:19000/` |
| Bucket | `terrabase-tiered-qualification` |
| Durability | Synced WAL, one device barrier per batch of 1000 |
| Records (exact bodies and versions verified) | 1,100,000 |
| Repeated-character payload bytes per record | 128 |
| Published segments | 4 |
| Compaction records | 1,100,000 |
| Synced seed time | 51.037 s |
| Synced writes/sec | 21553 |
| Compaction and verified object publication | 39.742 s |
| Reopen and exact scan after SSD cache deletion | 60.780 s |
| Exact scan count | 1,100,000 |
| Total interval | 152.127 s |
| Peak working set | 2,553,036,800 bytes |
| Idle SSD cache budget (active leases require working space) | 16,777,216 bytes |

The segment manifest was committed only after the RustFS object upload, then
the local SSD copies were removed. Reopen therefore verified the read-through
cache refill and segment checksum validation against RustFS.
The source still retains live documents and history metadata in RAM. This
functional qualification is not a larger-than-memory or 100-million-record claim.