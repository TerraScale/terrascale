# Local database encryption comparison (TS-861)

Measured 2026-10-23 on dev host `delta`, .NET SDK `11.0.100-rc.1.26425.128`, runtime `11.0.0`, commit `7d0d378d66d0ab3e657739e6d8e90af75eb77656` (Release). All three plain/aegis256 pairs completed, including payload and reopen row-count verification. This shared host was running other admitted jobs; timings include scheduling and storage contention. These are warm-filesystem-cache microbenchmark results, not production or cold-cache guarantees.

Exact admitted command (no keys in arguments):

```sh
TS_HEAVY_HOST=delta /home/mariogk/.local/bin/ts-heavy-any nice -n 19 bash -c 'set -euo pipefail; hostname; dotnet --version; git rev-parse HEAD; dotnet run --configuration Release --project benchmark/dotnet/DatabaseEncryption; dotnet build tests/Turso.Data.Sqlite.Tests/Turso.Data.Sqlite.Tests.csproj --configuration Release; dotnet run --project tests/Turso.Data.Sqlite.Tests --configuration Release --no-build -- --treenode-filter "/*/*/EncryptionTests/*" --minimum-expected-tests 6 --output detailed --no-ansi' --repo terrascale --branch feat/database-encryption
```

## Every measurement

Times are milliseconds. Rows below preserve program output and execution order.

| Repeat | Cipher | Write ms | Read ms | Checkpoint ms | Reopen ms | DB bytes | WAL bytes before checkpoint |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 1 | plain | 1121.381 | 524.250 | 39.664 | 2.266 | 14086144 | 22618832 |
| 1 | aegis256 | 1156.546 | 536.100 | 31.128 | 4.303 | 14090240 | 22622952 |
| 2 | aegis256 | 2614.680 | 382.236 | 174.080 | 14.248 | 14090240 | 22622952 |
| 2 | plain | 1378.560 | 575.339 | 257.911 | 4.698 | 14086144 | 22618832 |
| 3 | plain | 1038.305 | 551.218 | 180.625 | 4.245 | 14086144 | 22618832 |
| 3 | aegis256 | 1021.262 | 487.453 | 21.179 | 9.475 | 14090240 | 22622952 |

## Observed overhead

Percent difference is `(encrypted median / plain median - 1) * 100`, calculated from the displayed milliseconds. Each median has only three samples.

| Operation | Plain median ms | aegis256 median ms | Observed difference |
| --- | ---: | ---: | ---: |
| Write with commit | 1121.381 | 1156.546 | +3.14% |
| 10,000 point reads | 551.218 | 487.453 | -11.57% |
| Truncating checkpoint | 180.625 | 31.128 | -82.77% |
| Reopen and count | 4.245 | 9.475 | +123.20% |

The encrypted database is 4,096 bytes larger and its pre-checkpoint WAL is 4,120 bytes larger in every repeat. Reopen adds 5.230 ms at the median. Write times vary from 1,021.262 to 2,614.680 ms for encryption; checkpoint times vary from 21.179 to 257.911 ms across both modes. Lower encrypted read/checkpoint medians are observations under contention, not evidence that encryption accelerates those operations. This run cannot establish a stable production overhead percentage.

## Workload and key handling

Each repeat creates separate plain and aegis256 files with pooling disabled for both. Order alternates. Both store the same 10,000 ULID-shaped IDs and 1 KiB payload, use prepared statements, WAL, synchronous FULL, disabled automatic checkpointing, one write transaction, and 10,000 verified point reads. Timings include writes with commit, point reads, explicit truncating checkpoint, and reopen with row-count verification. File size and pre-checkpoint WAL size are bytes.

Each encrypted file gets a fresh process-memory random key. Keys are never printed or written separately; temporary database files are deleted in finally. The benchmark measures local driver encryption, not IMasterKey wrapping, OCI Vault delivery, production key rotation, or end-to-end TerraBase backup/recovery.

## Vendor regression result

The same admitted command built the vendor regression project successfully with zero warnings/errors, then ran six focused encryption cases: five passed, one failed, zero skipped; runner exit code 2, duration 655 ms.

Passed: builder option preservation; all three malformed/incomplete option cases; encrypted database and live-WAL copies requiring the original key (including raw-reader interoperability and missing/wrong-key rejection).

Failed: `RawConnectionCanCreateEncryptedBackup`, `EncryptionTests.cs:103`, expected `backup-payload` but got an empty result. The raw command executes one statement; its combined `CREATE TABLE; INSERT` setup created the table without inserting the row. The owned test now issues those statements separately. That test-only fix awaits parent push and a focused rerun; this report does not claim it passes yet.

Earlier attempts produced no measurements: commit `02c3dbb5f521c5d56efdc9e23daef3fc8807a6d4` failed vendor CS8604; `3f862dd02940830899e5639661e01784966d6618` failed benchmark IDE0011/IDE0300. Parent pushed the vendor guard; benchmark/test braces and collection syntax were fixed before the measured run. No production engine suite or CI result is claimed here.
