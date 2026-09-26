# RustFS producer to consumer integration receipt

## Environment identity

- Subject repository: `/home/mariogk/Projects/TerraScale/terrabase`
- Subject commit: `67e35f443565c54eb731d6ea2dd989cb5c87b7be`
- Subject worktree: dirty; the exact working tree digest is recorded in `run-receipt.json`.
- Runtime: .NET 11.0.0-rc.1.26425.128, NixOS 26.05, x64, `mariogk-dev`.
- Object provider: RustFS `rustfs/rustfs:latest`, container `terrabase-rustfs-20260922`, endpoint `http://127.0.0.1:19000`.
- RustFS readiness: HTTP 200; `ready=true`, storage `connected`, write quorum `true`.
- Credentials were read from the RustFS container environment and were not recorded here.

## Verified call path

1. `HostS3StorageTierIntegrationTests` sends JSON document writes and reads through the loopback ASP.NET HTTP host with a bearer identity.
2. The configured host resolves the database through `DefaultDatabaseProviderFactory`, builds `AmazonS3Client` and `S3NativeBackupObjectStore`, and injects `TieredStorageSegmentStore` into `StorageEngine`.
3. `StorageEngine.CompactAsync` writes a bounded local staging segment, uploads it through the authenticated S3 client, verifies length and SHA-256, then publishes the local manifest and truncates the WAL.
4. The test removes local segment copies, reopens the host, and performs a real HTTP GET. The tier downloads the immutable segment from RustFS, verifies and admits the cache file, and serves the JSON document.
5. The benchmark composition uses the same S3 adapter for segment objects and backup artifacts, then verifies scans, listing, queries, updates, deletes, full/incremental backup, full/PITR/final restore, compaction, and cold remote reopen.

## Commands and results

Host integration test, using credentials extracted from the local RustFS container without recording their values:

```sh
S3_ACCESS=$(podman inspect terrabase-rustfs-20260922 --format '{{range .Config.Env}}{{println .}}{{end}}' | awk -F= '$1=="RUSTFS_ACCESS_KEY"{print substr($0,index($0,"=")+1)}')
S3_SECRET=$(podman inspect terrabase-rustfs-20260922 --format '{{range .Config.Env}}{{println .}}{{end}}' | awk -F= '$1=="RUSTFS_SECRET_KEY"{print substr($0,index($0,"=")+1)}')
env TERRABASE_S3_ENDPOINT=http://127.0.0.1:19000 TERRABASE_S3_ACCESS_KEY="$S3_ACCESS" TERRABASE_S3_SECRET_KEY="$S3_SECRET" TERRABASE_S3_REGION=us-east-1 TERRABASE_S3_BUCKET=terrabase-tiered \
  dotnet run --project tests/TerraBase.Api.Tests/TerraBase.Api.Tests.csproj --configuration Release --no-build --no-restore -- --treenode-filter '/*/*/HostS3StorageTierIntegrationTests/*' --maximum-parallel-tests 1 --progress off --show-test-results all
```

Result: **3 passed, 0 failed, 0 skipped**, 3.201 seconds. The three cases covered publish/compact/evict/reopen/read, missing manifest rejection, and missing publication authority rejection.

Fresh end-to-end volume run:

```sh
S3_ACCESS=$(podman inspect terrabase-rustfs-20260922 --format '{{range .Config.Env}}{{println .}}{{end}}' | awk -F= '$1=="RUSTFS_ACCESS_KEY"{print substr($0,index($0,"=")+1)}')
S3_SECRET=$(podman inspect terrabase-rustfs-20260922 --format '{{range .Config.Env}}{{println .}}{{end}}' | awk -F= '$1=="RUSTFS_SECRET_KEY"{print substr($0,index($0,"=")+1)}')
env TERRABASE_S3_ENDPOINT=http://127.0.0.1:19000 TERRABASE_S3_ACCESS_KEY="$S3_ACCESS" TERRABASE_S3_SECRET_KEY="$S3_SECRET" TERRABASE_S3_REGION=us-east-1 \
  dotnet run --project terrabase/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj --configuration Release --no-build --no-restore -- --workload e2e-volume --object-tier s3 --documents 1000 --batch-size 100 --point-reads 64 --updates 20 --deletes 20 --queries 20 --relationships 20 --output bench/e2e-host-rustfs-20260923T0301Z
```

Result: **complete**, exit code 0. The detailed timings and checks are in `e2e-volume-summary.md`; the receipt digest is in `run-receipt.json`.

## Limits and next handoff

- A 100-million-record run was not performed in this receipt. Existing RustFS qualifications cover 1.1 million records and 100,000 records under a 2 GiB cgroup; they do not establish a 100-million-record throughput or memory claim.
- Startup admission validates remote descriptors and prefix authority but does not verify every manifest-referenced object exists; a missing object can fail on first read instead of startup.
- Host S3 configuration wires immutable segment tiering. The fresh end-to-end run composes `BackupManager` directly; it does not prove a host-injected S3 backup service.
- SSD cache limits cover idle segment files. WAL, active leases, index state, compaction, and backup/restore working sets are additional resource consumers.
