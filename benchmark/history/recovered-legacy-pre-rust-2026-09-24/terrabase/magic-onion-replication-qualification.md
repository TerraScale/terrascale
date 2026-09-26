# MagicOnion two process replication qualification

Run the retained local qualification from `terrabase/` after a Release build:

```text
nice -n 19 dotnet build bench/TerraBase.Benchmarks/TerraBase.Benchmarks.csproj -c Release
nice -n 19 dotnet bench/TerraBase.Benchmarks/bin/Release/net10.0/TerraBase.Benchmarks.dll \
  --workload magic-onion-replication-qualification --operations 8 \
  --output bench/receipts/magic-onion-replication-qualification
```

The driver starts two independent `TerraBase.Benchmarks` child processes. Each
child hosts the real `TerraBaseHost` on an HTTPS/HTTP2 listener with a
certificate from the retained test CA, a peer certificate allowlist, and a
separate `StorageEngine` WAL directory:

| Member | Listener | Durable directory | Authenticated client |
|---|---|---|---|
| `member-a` | dynamically allocated loopback port | `topology/member-a/data` | `member-b-client.pfx` |
| `member-b` | dynamically allocated loopback port | `topology/member-b/data` | `member-a-client.pfx` |

The seed is eight deterministic causal publications from `member-a`, with
sequence `n` depending on `n-1`. The driver then stops `member-b` and records a
failed authenticated push as the partition boundary. It restarts `member-b`
using the same durable directory, delivers sequences 8 through 2 in reverse
order twice, restarts `member-b` again, and verifies that the deferred sequence
2 is a durable duplicate after rehydration. Anti-entropy pulls the source
journal over the real MagicOnion `StreamAsync` method and replays the missing
set over unary `InvokeAsync` pushes. Sequence 1 unblocks the deferred suffix;
the final durable and applied frontiers must both equal the operation count.

Each retained receipt directory contains:

* `receipt.json` with topology, seed data identity, durable/applied frontiers,
  deferred and duplicate counts, repair lag, source/artifact hashes, and child
  commands;
* `summary.md` with the qualification table and stated limits;
* `raw-events.jsonl`, a deterministic ordered record of seed, partition,
  restart, reordered delivery, duplicate, repair, and final heartbeat events;
* `logs/` with stdout/stderr from every child generation;
* `topology/` with the two durable directories and test certificates.

The retained `20260914-magic-onion-qualification-local-v2` receipt has a
Release artifact path and digest plus commit/dirty state, but its environment
metadata omits CPU model and GC mode. It remains local correctness evidence
until rerun with the current workload. The current generator records CPU
model, server-GC state, and GC latency mode alongside the existing artifact and
source identity.

The qualification is local loopback process isolation. It does not claim WAN
latency, kernel packet loss, cross-region failure domains, or OCI behavior.
