---
status: accepted
---

# Gate capability and performance claims with evidence

TerraBaseV2 makes no RavenDB or TerraBase V1 parity claim. A native capability
is complete only when its current contract, implementation, tests, fault
evidence, security checks, resource bounds, and operations evidence all agree.
An accepted goal or ADR is a design decision, not implementation evidence.

Performance comparisons come only from reproducible TerraBase.Benchmark runs
with pinned source and environment identity, correctness validation, raw
receipts, warmup and sample policy, resource isolation, and an equivalent
workload. Local microbenchmarks guide development but do not support product
claims.

Decision-grade comparison for a workload starts only after its native V2
implementation passes functional, correctness, durability, recovery, security,
resource-bound, and protocol-conformance gates on the exact candidate source.
An unfinished or incorrect path cannot gain credibility from a fast timing.

Each required comparable PostgreSQL workload must finish `v2-faster` or
`no-improvement`, with every primary metric improved or statistically
non-inferior under TerraBase.Benchmark's five-percent band. A `v2-regression`
or `inconclusive` result does not pass.

Each required fair RavenDB comparison must finish `v2-faster` and prove either
at least twice the throughput by confidence interval or no more than half the
p95 and p99 latency. Other primary metrics must remain non-inferior under the
same five-percent band. RavenDB remains an external performance reference, not
a compatibility, feature-parity, wire, data, or migration target.

Multi-Region replication and reactive sync retain exact convergence evidence
alongside propagation latency, catch-up throughput, bytes, CPU, allocations,
tail latency, and foreground interference. A change must not regress against
the best approved valid baseline for the same topology and fault shape.

PostgreSQL remains a useful database comparison. Loro, Yrs, Automerge, and
other CRDT systems may be external research or benchmark references where
semantics match. None is a compatibility target or correctness oracle.

Expected golden values must be independently specified. A test does not prove
conformance when the implementation under test also generates its expected
output.
