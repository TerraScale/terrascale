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

PostgreSQL remains a useful database comparison. Loro, Yrs, Automerge, and
other CRDT systems may be external research or benchmark references where
semantics match. None is a compatibility target or correctness oracle.

Expected golden values must be independently specified. A test does not prove
conformance when the implementation under test also generates its expected
output.
