# Twenty-Region replication lane

The `terrabase-replication-bench` binary exercises the architecture ceiling:
20 Regions, two durable Members per Region, and direct fanout to all 40
Members. It runs `RegionalDurable` and `GlobalDurable` writes through the real
composed engine and CRDT path. It also checks one-Member loss, remote-Region
loss, origin-Region loss, retry with the same identity, and convergence after
the missing Members return.

Run it from the repository root:

```text
cargo run --release -p terrabase-cluster --bin terrabase-replication-bench -- \
  --operations 10 --samples 3 --warmup 2
```

Every successful run creates a new Markdown receipt under
`docs/evidence/benchmarks/history/`. The binary uses exclusive file creation
and never replaces an older receipt. `--history-dir DIR` redirects receipts
for an isolated test run without enabling a single overwriteable output file.

## Emulator contract

The lane builds a run-local `RegionCatalog` at the 20-Region ceiling. `GRU`,
`SFO`, and `FRA` are the production catalog entries. The other 17 codes are
admitted only inside the run and are never presented as selectable product
Regions.

The fixed GRU-origin RTT list is a deterministic input, not measured network
data. `GlobalDurable` sleeps for the maximum configured RTT once per logical
write, which models parallel direct fanout's network floor. The durable engine
and CRDT applications still execute on one host. Local disk scheduling,
filesystem caches, and forty same-host Members make the result diagnostic.

This lane cannot support a production or competitor speed claim. ADR 0026
still requires isolated resources, open-loop load, raw resource counters,
real WAN runs, candidate artifacts, confidence intervals, and real evidence
for every Region admitted to the public catalog.
