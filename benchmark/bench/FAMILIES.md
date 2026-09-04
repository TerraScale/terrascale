# E-13 benchmark families

The benchmark binary owns the operator entry point:

```text
cargo run --release --manifest-path bench/Cargo.toml -- --family ingest ...
cargo run --release --manifest-path bench/Cargo.toml -- --family indexed-query ...
cargo run --release --manifest-path bench/Cargo.toml -- --family sync ...
cargo run --release --manifest-path bench/Cargo.toml -- --render-history \
  --results bench/results --output bench/RESULTS.md
```

`auto-index` is a served-boundary shell family because its phases require
HTTP endpoints and server audit resources: run `bench/scripts/auto-index.sh`.
The scripts never start a substitute comparator. An absent endpoint or
adapter is retained as `unavailable`; a response, identity, or threshold
failure is retained as `failed`. Neither status supplies benchmark numbers.

Every measured launch member is admitted at exactly 2 OCPU / 12 GiB. The
resource wrapper verifies cgroup CPU quota, a two-CPU effective cpuset, and
the 12-GiB memory ceiling. Runs also require immutable 64-hex runtime and
comparator digests. `single-node` and `rf2` are explicit topology values and
are never joined into one comparison group.

Family result tables are rendered into `bench/RESULTS.md` by the same
`--render-history` command that renders the point-operation history:

| family | required measured facts |
| --- | --- |
| indexed query | p50/p95/p99 latency, rows returned/examined, pages read, logical work |
| bulk ingest | documents/sample, docs/s, p50/p95/p99 batch latency, before/after resource evidence |
| auto-indexing | seed time, write cost at 0/5/12 indexes, first indexed query, time-to-active, fixed-point replay, shadow evidence, zero-stale reads |
| sync | attach, immutable snapshot identity, WebSocket delivery, CDN log delivery, and cross-region p50/p99 samples |

