# SE-15 benchmark receipt: pinned reference runs (PostgreSQL 18, RavenDB 7.2)

PostgreSQL-parity-or-better: inconclusive

verdict: inconclusive

This receipt records the **reference side** of two lane runs. It carries no
TerraBase comparison and no verdict: the engine side of the lane had not run
when this was written, so the objective line stays `inconclusive` by
construction. The engine-side run and its gate classification are recorded
separately once they exist.

## Run identity

- Lane adapter: `src/benchmarks/` (engine side, verdicts), driver
  `bench/` (operator side, facts only).
- Source revision of the driver code: `0b41d60` (the RavenDB driver's
  explicit-collection fix is part of this revision; the PostgreSQL driver
  is unchanged from `bdd5b4d`).
- Receipt tier: lane-covered — loopback, single development host, no
  production claim.
- Redaction: none required; no credentials appear (loopback trust
  authentication for the benchmark role only; unsecured RavenDB on
  loopback inside a rootless container).

## Environment

- Host: 28 logical CPUs, Linux 7.2.1, ext4 on /dev/sda2; both the
  PostgreSQL data directory and the TerraBase engine locations live under
  `/tmp` on this device.
- PostgreSQL: started by `bench/scripts/pg-start.sh` (nix-shell
  `postgresql_18`), fresh data directory per run, `listen_addresses`
  loopback, `synchronous_commit=on`, `fsync=on`, `full_page_writes=on`,
  every other setting at the artifact's shipped defaults (captured
  verbatim at startup: shared_buffers 128MB, work_mem 4MB). Observed
  identity (verbatim): `PostgreSQL 18.6 on x86_64-pc-linux-gnu, compiled
  by clang version 21.1.8, 64-bit`.
- RavenDB: started by `bench/scripts/raven-start.sh`
  (`docker.io/ravendb/ravendb:latest`, rootless podman, host network,
  unsecured loopback, fresh data directory). Observed identity (verbatim
  from `/build/version`): `RavenDB 7.2.5, build 72029`.
- Client: the pinned drivers themselves — rust `postgres` 0.19 (one
  synchronous connection per concurrency slot, prepared statements,
  autocommit) and rust `ureq` 2 (one keep-alive connection per slot).

## Commands (repository root)

```text
./bench/scripts/pg-start.sh
TERRABASE_PG_REFERENCE_ENDPOINT=postgres://bench@127.0.0.1:55432/postgres \
  cargo run --release --manifest-path bench/Cargo.toml -- \
  --target pg --workload {read,write,query,mixed} --out bench/results

./bench/scripts/raven-start.sh
TERRABASE_RAVEN_REFERENCE_ENDPOINT=http://127.0.0.1:8091 \
  cargo run --release --manifest-path bench/Cargo.toml -- \
  --target raven --workload {read,write,query,mixed} --out bench/results
```

Lane configuration (`LaneConfig::RECORD_MINIMUM`, both sides): seed 1,000
documents · 10,000 operations per sample · 5 samples · concurrency 16 ·
1,000 warm-up operations excluded from every sample · 5% noise band.

## Results (median across the 5 samples; full per-sample facts in
`bench/results/*.json`)

| workload | side | ops/s | p50 µs | p95 µs | p99 µs |
| --- | --- | --- | --- | --- | --- |
| read | postgresql 18.6 | 99,595 | 148 | 215 | 279 |
| read | ravendb 7.2.5 | 12,468 | 446 | 1,685 | 22,518 |
| write | postgresql 18.6 | 2,577 | 6,000 | 6,219 | 8,978 |
| write | ravendb 7.2.5 | 3,058 | 4,967 | 6,797 | 8,499 |
| query | postgresql 18.6 | 94,621 | 153 | 227 | 300 |
| query | ravendb 7.2.5 | 3,192 | 1,236 | 25,840 | 34,080 |
| mixed | postgresql 18.6 | 8,184 | 154 | 6,042 | 6,394 |
| mixed | ravendb 7.2.5 | 3,547 | 1,765 | 15,011 | 21,236 |

## Known limits, stated

- Single development host, loopback, background editor tooling running:
  these are facts about this host at this moment, not production claims,
  and never authorization for deployment (PD-2026-08-26-03).
- Both references pay their client protocol on loopback; the TerraBase
  side of a comparison runs the in-process library boundary. The engine
  side's facts documents carry the same boundary statement.
- The RavenDB driver's first query-lane run failed (auto-index never
  served the collection because raw PUTs without explicit
  `@metadata.@collection` landed in `@empty` on this server build); the
  driver was fixed to name the collection explicitly and every RavenDB
  row above is from the fixed driver, fresh database per run. The failed
  run is retained here as a record, not hidden.
- Warm-up absorbs RavenDB's auto-index creation and the TerraBase
  auto-index lifecycle equally, per the pin.
