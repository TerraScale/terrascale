---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Decision-grade performance gates

## Authority and readiness

`TerraBase.Benchmark` is the sole authority for product performance claims. A
local timing, microbenchmark, profiler result, simulator run, development LXC
run, or unreviewed receipt is diagnostic evidence only.

A comparison starts only after the exact candidate, workload manifest, and
protocol versions pass their functional, deterministic-correctness, durability,
crash-recovery, security, authorization, resource-bound, tenant-isolation, and
cross-language conformance gates. A failure in any prerequisite makes the
performance result invalid rather than slow.

Every decision-grade run records immutable source commits, artifact digests,
database and driver versions, complete configurations, workload manifest and
seed, data and index state, cache state, durability level, topology, fault
shape, hardware allocation, storage image, kernel, measurement windows, raw
sample identities, environment validity, and confidence intervals.

## Fair workload contracts

One versioned workload manifest drives TerraBaseV2 and every comparison
adapter. It fixes logical operations, payloads, indexes, query meaning,
consistency, durability, error policy, retry policy, concurrency, offered rate,
dataset size, cache state, and validation digest. A competitor is compared only
when it can implement the same observable semantics without a product-specific
handicap.

Each claim also fixes its measured boundary. A database-level product claim
uses the official SDK, public Database API, private TBEP gateway, and engine;
it charges gateway resources to TerraBase. The comparison uses the competitor's
official supported driver and matching transport security. Engine-only and
codec-only runs are diagnostic or component gates and cannot support an
end-to-end database claim.

Competitors have two recorded lanes:

- documented defaults, reported as operational context; and
- production-safe tuning on the same resources, used for the primary claim.

Tuning may use documented indexes, batching, connection pools, prepared
operations, and durability settings that preserve the manifest. It may not
disable durability, response validation, conflict behavior, or correctness.
TerraBase receives the same opportunity. Every setting remains in the receipt.

The decision-grade resource shape remains exactly eight single-threaded vCPUs
and eight GiB of RAM per database VM. Subject and comparator use the same VM
count, per-VM shape, storage class, durability and failure-tolerance topology,
and network policy fixed by the workload. If equal observable semantics cannot
fit that topology, the workload is not comparable and supports no claim.

Every database process, gateway, workload generator, and metrics collector runs
in a separate VM or an equivalently isolated bare-metal allocation. Workload
generators and collectors never share the candidate's CPUs, memory, or storage.
The benchmark repository enforces exclusive CPU, storage, scheduler-wait, IRQ,
steal, thermal, frequency, and environment-validity controls. It reimages or
restores an equivalent clean storage state between randomized paired blocks.
Additional scale curves are useful but cannot replace the fixed release gate.

## Load and statistical method

Each latency or service-level workload uses an open-loop, Poisson-scheduled
offered rate. Latency begins at the scheduled start so queue delay is retained
and coordinated omission cannot hide overload. Skipped, late, failed, and
retried operations are reported and gated.

Each capacity workload separately uses closed-loop saturation to find maximum
throughput while enforcing its latency and error SLO. Saturation throughput is
never compared with idle or unconstrained latency.

Subject and reference samples run as randomized paired blocks on equivalent
fresh environments. The harness uses preregistered primary metrics, paired
95-percent bootstrap confidence intervals over ratios, deterministic resample
seeds, and Holm correction across the primary gates in one claim family.
Warmup, steady-state duration, sample count, cache state, and exclusion rules
are manifest fields fixed before the candidate runs.

Every workload declares exactly one primary performance question:

- capacity: throughput at the same p99 latency, error, and durability SLO;
- latency: p99 latency at the same achieved rate and concurrency;
- replication or sync: frontier latency at the same mutation rate and fault
  shape; or
- recovery: verified recovery or convergence time for the same state volume.

Correctness, acknowledged durability, error rate, late or skipped work, and
tenant isolation are unconditional gates. Named CPU, memory, allocation, byte,
write-amplification, and interference metrics are guardrails. An aggregate or
geometric mean never hides a failed workload, primary metric, or guardrail.

## PostgreSQL gate

Every required semantically comparable PostgreSQL workload passes individually
as `v2-faster` or `no-improvement`. Its primary metric and designated resource
guardrails are statistically non-inferior within the five-percent margin:

- higher-is-better confidence lower bound is at least `0.95`; and
- lower-is-better confidence upper bound is at most `1.05`.

An invalid, failed, regressed, or inconclusive receipt does not pass. PostgreSQL
is a performance reference, not a wire, SQL, relational, or compatibility
requirement.

## RavenDB gate

TerraBaseV2 must be materially faster than RavenDB on every required comparable
hot-path workload. Before either candidate runs, each workload manifest
preregisters one of these matched-load gates. The workload passes individually
as `v2-faster` only when that gate passes:

- throughput ratio confidence lower bound is at least `2.00` while both
  products satisfy the same p99, error, consistency, and durability SLO; or
- both p95 and p99 latency ratio confidence upper bounds are at most `0.50` at
  the same achieved request rate and concurrency.

All designated guardrails remain non-inferior within five percent. An aggregate
cannot compensate for a missed workload. A workload with different semantics
is marked not comparable and supports no speed claim.
RavenDB creates no V1 parity, migration, query, client, data-format, or feature
scope for TerraBaseV2.

## Regression baselines

Internal codec, storage, replication, sync, and recovery regressions compare
against the last promoted valid release baseline selected before the candidate
runs. The baseline must match the workload, topology, fault shape, durability,
hardware, storage, kernel, harness, protocol, and measurement method.

A baseline expires after thirty days or any material environment, database,
driver, workload, statistics, protocol, or harness change. The replacement is
approved independently; the candidate never selects the most favorable
historical sample. A rolling trend may detect drift but is not the release
reference.

No internal release baseline exists before the first promotion. The first
production candidate must pass every absolute, correctness, PostgreSQL, and
RavenDB gate without a self-comparison. Independent release approval then
promotes its matching receipts as the initial internal baselines. Every later
candidate must also pass the matching unexpired baseline. When an environment
or method change expires a baseline, the harness reruns the last promoted
source under the new conditions before it evaluates the candidate.

The existing receipt vocabulary remains exact:

- any primary or guardrail regression: `v2-regression`;
- any missing or inconclusive required result: `inconclusive`;
- at least one improvement and all other gates non-inferior: `v2-faster`; and
- all gates non-inferior without a proven improvement: `no-improvement`.

## Three-Region production qualification

The initial production-readiness gate covers one, two, and three selected
Regions, with the complete three-Region shape running two active Members per
Region. It includes real WAN runs for the initial GRU, SFO, and FRA catalog and
deterministic simulation for exhaustive fault schedules.

Receipts separately measure:

- local `RegionalDurable` acknowledgement;
- origin-to-remote `stored`, `applied`, and Merkle-`verified` frontiers;
- origin-to-authorized-client-visible latency;
- `GlobalDurable` acknowledgement in every selected Region;
- steady-state and backlog catch-up throughput;
- snapshot bootstrap, Region loss, rejoin, and repair;
- bytes, copies, allocations, CPU, and foreground interference; and
- clock synchronization uncertainty for cross-Region timestamps.

Direct fanout runs in parallel to every selected Region. Topology-specific
budgets are frozen before optimization and include both an absolute SLO and a
protocol-overhead limit above the measured QUIC RTT and durability floor. They
may tighten after evidence and may not be loosened to pass a candidate.

Under target steady-state load, no replication or sync backlog may grow. During
catch-up, the replication lane must use at least eighty percent of its admitted
bandwidth while foreground p99 degradation stays within five percent. The
candidate must also be non-inferior to the matching last promoted baseline
after the first baseline exists.

Twenty-Region qualification is not an initial release gate. It begins only
after TerraBaseV2 is production-ready and operating in the three-Region
catalog. Catalog expansion beyond three requires deterministic twenty-Region
capacity and fault qualification plus real evidence for each new Region before
that Region becomes selectable.

## Optimization completion

Optimization does not continue under the unbounded phrase “while a bottleneck
remains.” A performance work item is complete when all required gates pass,
profiles identify no safe actionable dominant bottleneck, and two reviewed
candidates fail to improve the primary metric by at least two percent without
losing a guardrail. Correctness, durability, authorization, verification,
isolation, fairness, backpressure, and recovery are never exchanged for speed.
