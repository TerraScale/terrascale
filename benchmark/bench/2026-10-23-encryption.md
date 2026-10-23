# Local database encryption comparison (TS-861)

Status: implementation ready; measurements pending the parent push and remote execution. No performance claim yet.

Run on one dev host through the managed admission wrapper, after the parent pushes the branch:

```sh
~/.local/bin/ts-heavy-any nice -n 19 dotnet run --configuration Release --project benchmark/dotnet/DatabaseEncryption --repo terrascale --branch feat/database-encryption
```

The program prints all three repeats as a Markdown table, plus host and runtime. Replace this pending status with that output and the measured commit SHA. Keep every repeat; compare medians only after checking all six rows.

Each repeat creates separate plain and aegis256 files with pooling disabled for both. Order alternates. Both store the same 10,000 ULID-shaped IDs and 1 KiB payload, use prepared statements, WAL, synchronous FULL, disabled automatic checkpointing, one write transaction, and 10,000 verified point reads. Timings include writes with commit, point reads, explicit truncating checkpoint, and reopen with row-count verification. File size and pre-checkpoint WAL size are bytes. This is a warm-filesystem-cache microbenchmark; it does not measure production workload or cold-cache behavior.

Each encrypted file gets a fresh process-memory random key. Keys are never printed or written separately; temporary database files are deleted in finally. Run command contains no key material.
