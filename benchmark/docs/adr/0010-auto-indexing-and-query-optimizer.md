---
status: accepted
---

# Automatic indexing and the query optimizer

## Decision

Automatic indexing is always enabled for every collection. Every collection
has an immediate engine-owned identity lookup path and one versioned index
catalog. The optimizer creates, selects, widens, merges, supersedes, and
retires query-shaped Auto-Indexes without customer action.

TerraBase does not eagerly index every observed JSON path. That would move
unbounded write, memory, and storage cost onto tenants who never query those
paths. Auto-Indexes follow admitted query shapes and measured reuse.

## Canonical index definitions

The optimizer lowers an authorized canonical TQL plan to required index
capabilities:

```text
AutoIndexDefinition {
    tenant_ulid
    database_ulid
    collection
    schema_generation
    ordered_fields_and_directions
    equality_and_range_capabilities
    full_text_analyzer_identity
    projection_covering_fields
    relationship_direction_and_name
    aggregate_shape
    definition_version
}
```

`AutoIndexUlid` is the catalog identity from ADR 0027.
`AutoIndexDefinitionDigest` is the domain-separated BLAKE3 digest of the
canonical definition. Equivalent query shapes in different Regions therefore
propose the same digest. The coordinated catalog decision selects or reuses one
ULID for that digest. The complete definition is compared even when digests
match; the same digest with different canonical bytes is corruption.

Definitions are versioned database metadata and replicate through native
TerraBase commits. Concurrent identical creation converges. Widening or merging
creates a new definition and an explicit supersession relation. Index contents
are rebuildable local derived state. Members do not replicate physical index
pages or trust another Member's applied generation without verification.

Declared forward and reverse relationship indexes and identity lookup are
mandatory engine indexes. They are created with their schema or collection
generation rather than waiting for an observed query.

## Selection

Every query selection produces a bounded `IndexSelectionRecord` containing:

- canonical query and plan identity;
- required capabilities and causal position;
- candidates considered;
- coverage, readiness, cost, and rejection reason for each candidate;
- the selected index or new build identity;
- required and observed applied generation;
- plan-cache use;
- bounded-scan admission when used; and
- tenant resource admission outcome.

An equivalent explicit index takes precedence. A query cannot use a
worse-than-available ready index without a recorded deterministic reason.
Customers may create explicit indexes for stable operational control, but
ordinary queries never require one.

Authorization affects the plan before selection. An index may be physically
shared only when key and payload layout cannot expose another tenant or an
unauthorized field. Index iterators enforce tenant, database, collection,
Partition, schema, and authorization scope.

## Missing-index behavior

When no ready index fully serves a query, TerraBase creates or reuses the
canonical Auto-Index build.

The query may scan only when all of these are true:

- the admitted collection slice has a strict row and byte upper bound;
- the cost estimate fits the tenant's CPU, memory, I/O, and deadline budget;
- authorization can be enforced before loading forbidden payloads;
- cancellation stops the scan within a measured bound; and
- the selection record names the estimate and actual work.

Otherwise the query waits within its deadline for the index's required applied
generation or returns `IndexBuilding` with typed progress and retry guidance.
The build is a durable long-running operation under ADR 0028. It never falls
back to an unbounded scan.

## Causal visibility

Each local index tracks:

```text
IndexProgress {
    auto_index_ulid
    definition_version
    build_generation
    applied_partition_positions
    source_schema_generation
    content_root
}
```

Session-consistent queries require the selected index positions to include the
caller's sparse causal dependencies. A query waits, chooses another equivalent
ready index, or returns a typed timeout. It never labels stale output as
session consistent.

Eventual visibility is an explicit request. Results still return the exact
index and causal positions used.

Index publication is monotone and crash safe. A stale builder cannot replace a
newer generation. Recovery either exposes the complete published generation or
resumes or discards its staging state.

## Lifecycle

The optimizer may widen or merge compatible Auto-Indexes when measured total
write, read, memory, and storage cost improves. It does not build one giant
index merely to reduce catalog count.

A replacement must reach at least the superseded index's applied causal
positions before new queries prefer it. Existing queries and reactive views
retain a bounded generation pin or restart against the replacement. The old
index retires only after active pins, checkpoint reconstruction, and recovery
requirements release it.

Idle and deletion decisions use measured query frequency, maintenance cost,
size, rebuild cost, dependency pins, and tenant budget with hysteresis. A query
can revive a retained idle index. A deleted index definition or content can be
recreated from canonical data without affecting database correctness.

Catalog and build work use bounded tenant-aware queues. Index construction,
merge, and deletion cannot starve writes, replication, repair, or another
tenant. When resource admission refuses a build, the query receives typed
operation progress or overload instead of triggering unlimited background
work.

## Reactive and authorization integration

Ordinary execution, reactive dependency tracking, and authorization use the
same catalog, canonical definitions, key ranges, and applied positions.
Maintaining separate shadow query models is forbidden.

An index publication or replacement that changes dependency identity creates
a new view-plan generation or a verified equivalent handoff. Authorization
generation changes invalidate affected cached plans and views even when the
physical index remains reusable.

## Evidence

Qualification covers deterministic definition identity across Regions,
concurrent creation, crash during build and publish, session read-your-writes,
bounded scan admission and refusal, stale-builder rejection, widening and
retirement handoff, relationship indexes, full-text capability, authorization
filtering, reactive plan continuity, hostile query shapes, and noisy-tenant
isolation.

Performance claims require TerraBase.Benchmark receipts for write
amplification, build time, storage, memory, selection latency, query latency,
maintenance cost, and recovery. RavenDB or TerraBase V1 behavior is not an
acceptance baseline.
