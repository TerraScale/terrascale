# TerraBase Engineering Goal

## 1. Product objective and priorities

Build TerraBase as a production-ready, Rust-native, multi-region database specialized for transactional application data: primary-key lookups, selective indexed queries, short ordered range scans, and small atomic mutations. Provide document ergonomics, explicit schemas, relationships, automatic indexing, and reactive queries. Optimize this workload before expanding general-purpose query features.

Product quality, correctness, memory efficiency, predictable latency, sustained throughput, and low operating cost take priority over development time or implementation cost. Build TerraBase's own storage and replication engines with native Rust libraries. SQLite is the mandatory external correctness and performance baseline; it is not TerraBase's storage backend.

**Competitive objective: outperform tuned SQLite in every applicable case of the declared transactional benchmark matrix, while meeting or improving the declared memory and resource budgets and preserving equivalent guarantees.** Include tail latency, sustained throughput, startup, and recovery. An average win cannot hide a losing workload. Keep every loss visible and actionable until resolved. A claim about every imaginable benchmark is not testable; section 10 defines the complete, versioned acceptance matrix and the evidence required to claim a win.

Start with **SSD + S3 as the two storage tiers**: SSD provides fast local persistence and caching, and S3-compatible object storage is the durable home for the bulk dataset. RAM is a bounded working resource. Keep the architecture extensible for an optional HDD third tier in a later phase. Total data and retained history must be able to grow far beyond RAM and beyond each worker's local cache. Scale compute, SSD capacity, and object-storage capacity independently. Use benchmark evidence to select the best physical techniques for each workload and tier within TerraBase's own Rust-native engine.

## 2. Rust implementation and dependency policy

Use a Cargo workspace, Rust 2024 edition, a pinned stable toolchain, an explicit minimum supported Rust version, and a committed lockfile. Select and pin compatible crate versions when implementation starts; verify behavior against those versions and record the source revision, feature flags, and supported platforms.

Use maintained Rust-native libraries and implement the production storage and replication algorithms in Rust. Standard OS interfaces and required cryptographic/platform dependencies must have their native components and features documented. Keep benchmark-only SQLite bindings outside production dependencies.

Use the following starting stack, qualifying each integration through the real engine caller:

| Area | Starting choice and requirement |
| --- | --- |
| Networking and bounded asynchronous I/O | `tokio`, `tower`, and `bytes`; isolate blocking disk work and CPU-heavy maintenance from network executors. |
| Internal gRPC | `tonic`, `prost`, and their matching code-generation crates, with checked-in versioned `.proto` contracts. |
| TLS and external HTTP | `rustls` and `reqwest` with explicit TLS features; authenticated member connections and bounded external clients. |
| S3-compatible storage | The Rust `object_store` crate behind a narrow engine storage contract; use `aws-sdk-s3` only where a required operation is not adequately supported. Avoid redundant client stacks without evidence. |
| Hot storage and replication compression | `lz4_flex` with an explicitly versioned frame/envelope format and decompression bounds. |
| Boundary serialization and identifiers | `serde`, `serde_json`, and a Rust `ulid` implementation; persisted records use the engine's versioned binary format. |
| Diagnostics | `tracing`, bounded exporters, and metrics for memory, storage, query execution, replication, and costs. |
| Verification and measurement | `proptest`, `loom` where applicable, `cargo-fuzz`, Criterion, and process-level benchmark drivers. |

Keep abstraction at actual replacement boundaries: storage providers, transport, index advice, clocks, fault injection, and testable I/O. Prefer concrete data layouts and ownership within hot paths. Use shared-nothing partition ownership where measurements support it; benchmark worker count, scheduling, NUMA behavior, and idle overhead rather than assuming one thread per core is always best.

Require `cargo fmt --all --check`, strict Clippy checks for supported targets/features, and relevant tests in CI. Apply workspace Rust/Clippy warnings, including `unsafe_code = "warn"`, `clippy::all`, and `clippy::pedantic`, with narrow, explained exceptions. Unsafe code requires a documented safety invariant, a measured reason, and targeted verification. Minimize allocations, copies, pointer indirection, lock contention, and unnecessary reference counting through profiling.

Bound task creation, blocking-worker concurrency, logging queues, metrics cardinality, and shutdown work. Handle cancellation, partial failure, and resource release explicitly. Do not hold synchronous locks across suspension or let background work monopolize foreground resources.

## 3. Memory as an engine contract

Low, predictable total memory use is a primary acceptance requirement. Rust ownership alone is not a memory budget.

Implement a process-wide memory controller with enforceable allocations for database/shard state, read caches, write buffers, transaction versions, indexes, replication, compaction, snapshots, queries, uploads/downloads, and observability. Subdivide fairly across tenants and workers. Reserve bytes before materializing or admitting work; account for in-flight and waiting operations as well as queued payloads. Bound both item counts and bytes.

Account for allocator fragmentation/retention, thread stacks, runtime/network overhead, mapped resident pages, and transient codec buffers. Leave measured headroom below the container limit. Track process RSS/PSS and container memory, including relevant filesystem cache accounting, separately from allocator live bytes and cumulative allocations.

Use compact encoded records, page/block buffers, fixed-width identifiers, and ownership transfer or borrowed views when lifetimes permit. Avoid permanent decoded-document mirrors and per-record heap objects when an on-disk index or compact representation is sufficient. Shared slices must not accidentally pin large buffers indefinitely.

Put historical replication records, causal metadata that can be paged, transaction recovery records, and change-feed history on disk with bounded caches. Bound snapshots, transaction age, subscriber queues, and retained versions. Never reclaim a version still required by an admitted snapshot or replica.

Under pressure, evict clean caches, throttle background work, spill supported operators to SSD, and backpressure or reject new work with explicit errors. Dirty data must cross its durability barrier before eviction. Disk spill needs its own capacity budget. Admission must fail predictably before an out-of-memory or disk-full failure threatens acknowledged data.

Acceptance includes idle footprint, bytes per open database/shard, steady and peak RSS, allocations and copied bytes per operation, and memory after a workload subsides. Demonstrate bounded memory during sustained ingestion, compaction, index construction, cold restore, and a stalled replication peer with data well beyond RAM.

## 4. SSD and object-storage architecture

### Storage roles and durable publication

Use RAM for a bounded hot working set; SSD/NVMe for the WAL, mutable state, hot index/data pages, temporary work, and a verified object cache; and S3-compatible storage for immutable data/index objects, sealed log chunks, manifests, snapshots, and retained recovery history. S3 participates in normal reads, recovery, and scaling, not only backup. SSD + S3 is the initial production implementation and acceptance target.

Keep frequently accessed lookup metadata and index blocks on SSD when that avoids unnecessary S3 requests for colder document bodies. Bound compaction and transfer work between SSD and S3. Tier placement must preserve the selected acknowledgment profile, snapshot visibility, and safe reclamation rules.

Do not require the complete database or all indexes to reside on every node's SSD. Fetch required ranges/blocks on demand, verify them against trusted checksums, cache within budgets, and coalesce concurrent requests for the same content. Support reopening from remote metadata and lazy hydration without downloading the whole dataset.

Treat object storage as an immutable-object API, not a mounted filesystem or a random-write block device. Design formats with block indexes, independently verifiable ranges, and bounded decompression. A whole-object checksum alone does not authenticate a partially fetched range. Avoid one GET per record, one object per mutation, and unbounded small-object metadata.

Batch WAL archival and object publication. Use bounded multipart uploads, request concurrency, read-ahead, and compaction. Upload and verify referenced content before publishing the manifest that makes it reachable. Use content identities, immutable generation metadata, and a tested atomic publication protocol. A mutable manifest pointer requires a verified conditional-write capability and fenced ownership; never assume listing order, rename semantics, or last-writer-wins metadata is sufficient.

Persist schema/index definitions, accepted initial index decisions, membership/placement epochs, and recovery frontiers needed to reconstruct a valid database. SSD cache loss may reduce performance; it must not invalidate data already covered by the selected remote durability guarantee. Restore indexes from durable artifacts or rebuild from authoritative data with explicit degraded-performance status.

### Acknowledgment and failure domains

Every database must declare its acknowledgment profile and required failure domains. Keep received, durably stored, applied, and object-archived frontiers distinct:

| Profile | Minimum acknowledgment condition | What recovery may rely on |
| --- | --- | --- |
| Local durable | Transaction and required metadata are committed to a synced local SSD WAL. | The local durable device; this profile does not promise device or region-loss survival. |
| Replicated durable | The commit is durably recorded on the configured replicas in named failure domains before acknowledgment. | Those replicas and their durable logs; object archival may lag. Region-loss survival requires acknowledgments outside that region. |
| Object durable | The commit and necessary recovery authority are verified in object storage and recoverably published before acknowledgment. | The stated object-store durability/availability scope, including the remote log tail; region-loss survival depends on the provider topology. |

Require explicit production profile selection and enforce its acknowledgment contract on every commit. Object-durable commits must cross the remote publication barrier before acknowledgment. Group commit and pipelining may amortize barriers but cannot acknowledge before the requested barrier. Report object-archival lag and exposure for other profiles, and bound outstanding unarchived data with backpressure.

Define recovery-point and recovery-time objectives per profile. An uploaded data segment without its recovery metadata is not a recoverable commit. Object storage does not make a database transaction atomic by itself.

### Cost, scaling, and reclamation

Measure SSD bytes, object bytes, logical-to-physical amplification, compaction and tier-transfer bandwidth, GET/HEAD/LIST/PUT counts, multipart overhead, and inter-region transfer/egress. Report cost per retained GiB-month and per workload at an explicit price schedule. Optimize request and transfer costs alongside capacity; object storage is not automatically cheap for tiny random requests.

Scale workers and shard placement independently from stored capacity, using measured heat and hysteresis. Bootstrap replacement replicas from a consistent remote checkpoint plus the durable log tail; do not force a surviving peer to stream the entire dataset. Prefetch only when measurements justify its bytes, memory, and cost.

Remote garbage collection must prove that no live generation, replica, snapshot, backup, or recovery frontier references an object. Use durable reachability/retention state, safe grace periods, and restartable deletion. Clean up abandoned uploads and orphan objects. Keep tombstone/causal reclamation safe against late or retired replicas; a stale replica must rebootstrap rather than resurrect removed data.

### Future optional HDD third tier

Keep placement policies, storage-provider boundaries, and tier metadata extensible so an HDD warm tier can be added after SSD + S3 is qualified. HDD implementation, deployment, and benchmarks are outside the initial milestones and acceptance gates. Do not require an HDD device to build, test, or operate the initial product.

If introduced, evaluate HDD for warm immutable segments and sequential access while retaining hot lookup metadata on SSD. Compare SSD+S3 with SSD+HDD+S3 under matched workloads, including HDD seeks, transfer and compaction costs, tail latency, failure recovery, and total storage cost. Adopt the third tier only where measured benefits justify it and all durability/visibility guarantees remain satisfied.

## 5. RustFS and S3 qualification

Use **RustFS as the required local and CI S3 integration target**, in reproducible containers within the Firecracker test environment defined in section 10, with a pinned release and image digest, readiness checks, isolated buckets/prefixes, and deterministic fixture cleanup. Local development and tests must not require a paid cloud account.

Exercise the real Rust object-storage client, ordinary reads/writes, immutable publication, log archival, cold startup, SSD-cache loss, backup, restore, and garbage collection against RustFS. An in-memory fake is useful for unit tests and fault injection but does not qualify the S3 path.

Verify the exact operations TerraBase relies on: range reads, multipart upload/abort, conditional publication if used, pagination, metadata, authentication, errors, and checksum handling. Do not assume an ETag is a content hash. Consult RustFS's compatibility matrix and probe required semantics against the pinned version.

Run restart, timeout, interrupted upload, corrupt/missing object, credential failure, and capacity-pressure campaigns. Test distributed RustFS failure behavior when that topology is part of the deployment qualification.

Qualify each intended production S3 provider separately before claiming support. RustFS success does not establish identical AWS S3 semantics, latency, durability, or billing. Benchmark RustFS on disclosed hardware and isolate/account for its CPU, RAM, and SSD usage; a shared-host RustFS run is not WAN evidence or proof of production storage cost.

## 6. Transactional engine, formats, and query execution

Build TerraBase's own Rust-native storage engine, using **both Bf-Tree and ZoneTree as required research and inspiration**. Implement the selected mechanisms as TerraBase-owned Rust components with native Rust libraries. Upstream implementations may serve as research comparators; the production core must implement TerraBase's architecture and guarantees.

Study [Bf-Tree's paper](https://www.vldb.org/pvldb/vol17/p3442-hao.pdf) and [source](https://github.com/microsoft/bf-tree) for variable-sized mini-pages, selective record caching, buffered updates, ordered access, and concurrent execution. Study [ZoneTree's architecture, source, and benchmarks](https://github.com/ZoneTree/ZoneTree) for ordered LSM organization, mutable/read-only segments, multipart disk layout, lookup indexes, block caching, merge scheduling, WAL, transactions, and compression. Record the examined revisions, mechanisms, assumptions, and experimental evidence.

**Use Bf-Tree-inspired techniques where they win and ZoneTree-inspired techniques where they win.** Benchmark the choice by access pattern, data size/skew, memory budget, read/write mix, concurrency, durability profile, and storage tier. Select physical layouts and access paths at explicit index, partition, or tier boundaries; a single global winner is not required. Include WAL, recovery, transactions, secondary-index updates, cache misses, maintenance, and resource costs. Results with durability disabled cannot qualify a durable production path.

Treat Bf-Tree-inspired caching/page organization for hot SSD point/range workloads and ZoneTree-inspired immutable segments for sustained writes and S3 publication as hypotheses to test. Support a combination where it outperforms the individual strategies under the same budgets. Do not hard-code a winner by tier or maintain duplicate representations without accounting for their memory, storage, write, and recovery costs.

Keep a unified transaction, visibility, durability, recovery, and replication contract across all qualified physical strategies. Range scans and transactions crossing layouts must preserve ordering, version selection, atomicity, and constraint semantics. Define authoritative versus cached/derived representations so each logical record is visible exactly once at a given snapshot, including during tier movement.

Publish the benchmark-derived selection policy and its applicable workload envelope, with operator controls and observable decision reasons. Online layout changes require measured benefit after conversion costs, hysteresis, bounded resources, snapshot-and-change catch-up, atomic activation, and crash-safe recovery. Qualify static configurations first; automatic adaptation must pass workload-shift and interrupted-transition tests. Keep experiments and results in `../ts-benchmark`, with SQLite as the mandatory acceptance baseline.

Define local transaction isolation, conflict detection, atomic publication, durable logging, checkpoints, crash recovery, corruption detection, and safe space reclamation before tuning throughput. Evaluate group commit under both low load and saturation; it must not trade unreported single-operation latency for a throughput headline.

Store documents in a versioned binary representation with direct field access, selective decoding/projection, nested paths, and efficient updates. Avoid repeated JSON-text parsing on the engine path. Define numeric precision, missing/null, comparison, collation, multikey, and array-element correlation semantics consistently across scans, indexes, replication, and public APIs.

Use canonical 16-byte ULIDs for entity identity and Protobuf bytes encoding with validated public string conversions. ULIDs are not causal clocks, transaction sequences, or evidence of global order. Persisted metadata must not depend on Rust type layouts, platform endianness, or compiler versions.

Support explicit versioned schemas, validation, schema evolution, generated typed contracts, relationships, reverse lookups, and integrity/delete policies. Documents, relationship state, unique/constraint indexes, and durable change records must commit consistently within the declared transaction boundary.

Prioritize point reads, equality/range/composite/covering access, bounded pagination, and small transactions. Add joins, sorting, aggregation, full-text, and broader query support only with bounded execution and evidence for the application workload. Use streaming and SSD spill where supported. Expose `EXPLAIN` and `EXPLAIN ANALYZE` with estimates, actual rows, index choice, I/O, memory, spills, and remote-object work.

Version and checksum WAL, pages/blocks, manifests, snapshots, and transfer envelopes. Validate lengths and bound decompression before allocation. Keep LZ4 via `lz4_flex` as the initial hot-path codec; benchmark block sizes, thresholds, and uncompressed small/already-compressed data. A colder-tier codec is admissible only with measured total-cost benefits and a tested, explicitly versioned Rust-native implementation.

## 7. Anna-informed leaderless replication

Use the primary **Anna: A KVS for Any Scale** paper and **Autoscaling Tiered Cloud Storage in Anna** as design foundations. Adapt private partition/worker state, hierarchical partitioning, lattice/CRDT merge semantics, asynchronous batched dissemination, selective hot-key replication, and measured tier placement/elasticity.

Map each adopted mechanism to its assumptions, Rust implementation, failure tests, and benchmarks. Anna's published throughput does not transfer to TerraBase. Its evaluated memory/EBS hierarchy does not establish an SSD/S3 architecture; validate that adaptation independently.

Allow leaderless writes for explicitly supported convergent data types and conflict policies. Define causal dependencies, version identities, deduplication, deterministic merges, tombstones, and concurrent-write behavior. Prove merge associativity, commutativity, and idempotence for supported operations, and verify convergence after arbitrary duplicate/reordered delivery and partition healing.

Provide bounded anti-entropy, durable range repair, replica bootstrap, failure detection, epoch-fenced membership changes, shard transfer, and restartable rebalancing. A slow/offline peer must accumulate recoverable backlog on durable storage, not unbounded RAM. When retention expires, require an explicit consistent resnapshot protocol. Bound causal metadata growth without losing evidence required for safe reconciliation.

Expose the transaction/consistency scope explicitly. Leaderless eventual or causal convergence does not provide global uniqueness, linearizability, or arbitrary serializable transactions. Use a proven coordination/consensus protocol for metadata or transaction invariants that require it; define participant sets, commit recovery, and behavior during partitions. Quorum counts alone do not prove strong consistency.

Keep optional coordinated guarantees distinct from the normal convergent path. Never benchmark an eventually consistent operation as an equivalent replacement for SQLite's stronger transaction guarantees. Specify cross-key, cross-shard, and cross-region atomicity and visibility; unsupported combinations must be rejected or explicitly scoped.

Demonstrate actual authenticated multi-node replication, catch-up, and placement execution. In-process merge helpers or a placement planner without a runtime caller do not satisfy this milestone.

## 8. Efficient gRPC replication and transfer

Use `tonic`/`prost` gRPC over HTTP/2 for member replication, repair, snapshot/shard transfer, and control traffic. Use long-lived, reused connections and bounded streaming RPCs; avoid a new connection or one unary RPC per document.

Define typed Protobuf envelopes for database/shard identity, schema/format version, membership epoch, causal/transaction identity, cursor ranges, acknowledgment frontiers, and payload metadata. Carry the engine's binary record batches directly in Protobuf bytes fields where appropriate. Configure `bytes::Bytes` where supported and measure serialization/copy costs rather than claiming end-to-end zero-copy.

Batch by both encoded bytes and count with a bounded flush delay. Pipeline network, local durable append, apply, and acknowledgment while preserving ordering dependencies and transactional visibility. Separate foreground replication, bulk bootstrap/repair, and control scheduling so large transfers cannot starve commits or failure detection.

Implement application-level byte credits and bounded per-peer/global in-flight work in addition to HTTP/2 flow control. Limits must cover queued, blocked, encoded, decoded, and decompressed data. Bound concurrent streams and outstanding repair ranges. Cancellation and disconnects must release reservations without losing durable cursor state.

Distinguish receipt from durable and applied acknowledgments. Advance a durable cursor only after its required persistence barrier. Support reconnect/resume, duplicate detection, idempotent replay, bounded retries with backoff/jitter, and repair when a cursor has fallen behind retention. Do not assume gRPC supplies exactly-once processing or transaction semantics.

Use mutually authenticated TLS, tenant/database authorization, certificate rotation, deadlines for bounded operations, progress/idle timeouts for long streams, health checks, and graceful draining. Validate declared lengths, codec versions, checksums, and message limits before expensive processing.

**Do not assume Tonic supports LZ4 as a built-in gRPC message encoding.** Initially use a versioned application batch envelope with explicit identity/LZ4 codec, compressed and uncompressed lengths, and checksum, implemented with `lz4_flex`. Negotiate capabilities and keep transport compression disabled for already-compressed payloads. A future custom gRPC encoding requires verified support and interoperability tests for the pinned stack.

Measure batch size and delay, concurrency, flow-control windows, compression, copied bytes, CPU/operation, queue delay, WAN goodput, and durable/apply lag. Tune against actual workload and network profiles; increasing buffers cannot substitute for bounded resource accounting.

## 9. Initial indexing with Von/JEV, then measured workload adaptation

### First decision without query history

Use Von as the preferred optional advisor and JEV as an optional alternative for **one durable initial decision per collection/schema generation**. An initial decision may contain a bounded set of indexes. The database must also work with a deterministic schema/statistics advisor and no model service.

Generate initial candidates from schemas, constraints, indexing policies, field metadata, bounded document samples, and deterministic statistics. No application query history is required. Compute types, nested paths, missing/null frequency, cardinality estimates, skew, value size, array expansion, and write rate in bounded incremental passes. Never infer enforceable uniqueness or referential integrity from a sample or model.

Use Rust contracts such as `IndexCandidateSource`, `InitialIndexAdvisor`, `WorkloadIndexAdvisor`, and `IndexLifecycleManager`. Keep model-specific HTTP types outside engine contracts. Advisors may rank valid candidate IDs or abstain; deterministic code owns validity, constraints, cost, admission, and physical changes.

Run initial evaluations through a bounded Rust queue with deadlines, cancellation, bounded retry, and fair tenant admission. Use an operator-managed shared Von service where configured; keep inference outside the database process and off reads/writes. Pin actual model and question-template versions. Use typed Rust HTTP clients for the verified Von/JEV protocols.

Persist decision state, schema generation, candidates, selected definitions, model provenance, fallback reasons, and activation identity. Fence concurrent evaluators so a generation accepts one initial decision. Timeout, outage, invalid output, uncertainty, or exhausted resources must select deterministic fallback without blocking database availability. Once that decision is durable, later model responses or recovery must not rerun/replace it as another initial decision.

Send only approved, minimal metadata/statistics; external JEV requires tenant authorization. Treat document text and metadata as untrusted input. Validate responses against supplied candidate IDs; never execute generated code/DDL or accept altered security/constraint rules. Track the separate model server's memory, latency, and cost.

### Required hosted Von test service

**Von is mandatory for live indexing integration tests and comparative benchmarks. The implementation work includes provisioning, starting, and hosting a working Von service so these tests can run; supplying an adapter or asking the user to provide a running endpoint does not satisfy this requirement.** Von remains optional for production database availability, and JEV credentials are not required for the Von test suite.

Provide reproducible local and CI deployment definitions and service lifecycle commands. Pin the server source/image and model artifacts, cache downloaded weights outside the source tree, set explicit CPU/RAM limits, check health, and complete a real warm-up inference before declaring readiness. Keep the model runtime in the external service. Document start, status, stop, reset, endpoint configuration, and startup-failure diagnostics. Bind local tests to loopback; a shared test host requires authenticated HTTPS and a managed service lifecycle.

Run the real Rust advisor client against this hosted service. Demonstrate accepted Von advice flowing through candidate validation, durable initial-decision recording, index construction, and held-out query evaluation. Record the actual response model version, decision provenance, inference latency, server CPU/RAM, and indexing/query outcomes. Compare with deterministic initial indexing under the same database budgets and include the model service in total resource costs. Keep benchmark drivers, datasets, profiles, and results in `../ts-benchmark`.

Test outage, timeout, invalid-response, and restart/fallback behavior separately. A required live-Von run must fail clearly if the service cannot start or no model-assisted path was exercised; skipped tests, mocks, and silent deterministic fallback cannot qualify live integration. Keep the hosted fixture available for the test session and provide reproducible teardown without disrupting a shared server used by other tests.

### Proven physical indexes and workload-driven decisions

After the initial decision, use conventional statistics, normalized query patterns, cost-based candidate selection, and measured shadow execution to adapt indexes. Apply the practical lifecycle of automated index management: recommend, build, validate, activate, monitor, and retire. Von/JEV must not become an ongoing per-query tuner.

Use mature physical techniques: ordered equality/range indexes, composite and covering indexes, unique and partial indexes, nested-path/multikey indexes, relationship indexes, and full-text structures where required. Choose layouts and algorithms through representative benchmarks. Neither a paper nor a model makes an index automatically faster.

Estimate selectivity and benefit deterministically, then verify real query results and improvements on representative retained workloads. Consolidate overlapping indexes; use minimum evidence, hysteresis, and build/retirement cooldowns to avoid churn. Keep manual/pinned indexes and protect constraint-enforcing indexes. Learned benefit prediction is optional only after it beats the deterministic approach on held-out evidence at equal total cost.

Budget logical index bytes, write amplification, physical storage, memory, SSD scratch space, S3 requests, and build concurrency separately. Admit document/index mutations against their logical cap before commit; WAL, retained versions, compaction, and backups need separate physical budgets. Never save space by silently omitting index entries and returning incomplete results.

Build from a consistent snapshot, durably capture concurrent changes, catch up to a defined watermark, verify, and publish atomically. Make builds resumable and cancellation-safe. Keep the active index until replacement is qualified. Detect and reverse measured regressions. Persist lifecycle state and recovery evidence across restart and replica movement.

Keep index freshness and query consistency explicit. An asynchronous index must expose its watermark and support waiting, a correct source fallback, or an explicit stale-read contract. Replicas may differ in physical acceleration, but identical logical queries at the same visibility cut must agree.

Acceptance compares no secondary indexes, deterministic initial indexing, Von/JEV-assisted indexing, and later workload adaptation at equal budgets. Hold evaluation queries out of initial selection. Measure first-query latency, coverage, sustained writes, build/catch-up time, memory, total storage/cost, and performance after workload shifts. Report when model assistance or an index adds no benefit.

## 10. Benchmark-driven engineering and SQLite acceptance

### Required Firecracker test environment

**Run tests and benchmarks in Firecracker microVMs configured with 4 vCPUs and 8 GiB RAM (8,192 MiB) per microVM.** Set `vcpu_count = 4` and `mem_size_mib = 8192`, and verify the effective configuration before execution. Apply the same limits to TerraBase, SQLite, and other comparison runs. For multi-node replication tests, use a separate microVM with these limits for each database node and report aggregate resources as well as per-node measurements.

Use a pinned Firecracker version, guest kernel, and reproducible root filesystem. Record image checksums, CPU model/topology and affinity, guest configuration, SSD backing devices, filesystem/mount options, cache state, and network topology. Disable guest swap and dynamic memory resizing during acceptance runs. Control host CPU contention and account for host page-cache behavior and virtualization overhead; 8 GiB of guest RAM is not an 8 GiB total-host-memory claim.

Provision and run the environment as part of the test harness. Keep RustFS, Von, and service load generators in separate Firecracker microVMs with the same per-VM limits, so their work does not consume the database VM's allocation. Report their usage and total deployment costs separately. Embedded benchmark drivers run inside the engine VM and count toward its budget. Keep service placement and resource isolation equivalent across comparison runs.

Use the fixed environment for unit/integration tests, process/crash recovery campaigns, microbenchmarks, and full workload qualification. Test binaries may be built beforehand. Record VM resets and volume reuse explicitly, and distinguish clean starts from restart/recovery scenarios. Missing Firecracker/KVM support must be reported as an environment failure; an unrestricted host run cannot silently satisfy the gate. Keep benchmark VM definitions, launch scripts, run manifests, and results in `../ts-benchmark`.

### Mandatory experiment loop

Before a performance-sensitive change, define the bottleneck/hypothesis, affected benchmark cases, semantics, and expected resource tradeoff. Capture the current Rust baseline and tuned SQLite baseline, profile, change the implementation, then rerun correctness and performance checks. Preserve failed experiments and useful negative results.

Every accepted optimization needs a reproducible improvement on its target case and no unexplained material regression in relevant cases. A useful intermediate improvement does not declare the final SQLite objective complete. Register benchmark changes and thresholds before evaluating a candidate; do not remove a losing case, change guarantees, or retune only TerraBase to obtain a win.

Keep microbenchmarks for diagnosis and full-process/service benchmarks for acceptance. Use release builds and documented compiler/CPU flags for both engines. Measure idle, steady state, overload, and recovery; short runs that end before cache exhaustion, checkpointing, or compaction are insufficient.

### Fair SQLite baseline and measurement boundaries

Use a pinned, maintained SQLite release with prepared statements, appropriate indexes, transactions/batching, WAL mode and synchronous settings matched to the tested durability, and JSONB where appropriate. Compare SQLite as both relational rows and indexed documents where those representations match the logical workload. Validate results independently.

Match hardware, the Firecracker 4-vCPU/8-GiB limits, SSD, dataset, key distribution, document size, query semantics, isolation, batching, cache conditions, and concurrency offered. Give SQLite appropriate tuning and compare equivalent physical indexes separately from automatic-indexing strategies. Include a single-client case and concurrent readers/writers; report saturation and error rates.

Measure embedded TerraBase calls against embedded SQLite calls for engine claims. For API/service claims, use an equivalent SQLite service path and account for serialization/network work. Report distributed TerraBase profiles separately: SQLite has no native multi-region replication or object tier. Do not call extra WAN/S3 durability equivalent to a local SQLite commit or claim those feature gaps as numeric speedups.

Report hot/cold RAM and SSD cache states explicitly, including OS cache handling. Use equal total memory budgets, datasets below and well above RAM/SSD-cache capacity, realistic offered-load scheduling, and latency measurement that avoids coordinated omission. Verify equal completed work and report failures, timeouts, retries, and overload rejections.

Compare TerraBase's Bf-Tree-inspired, ZoneTree-inspired, and combined physical strategies on the same workload matrix. Use SSD-local measurements to isolate engine behavior and SSD+S3 measurements to qualify the initial production topology, including data larger than local caches and changes in access patterns. Include layout conversion, tier movement, duplicate cache/metadata costs, and background maintenance in the totals. Record where each strategy wins, loses, or lacks evidence, and use these results to qualify the selection policy. Upstream research comparisons supplement the SQLite baseline. Add third-tier benchmark cases only when the future HDD extension enters implementation.

### Required workload and resource matrix

| Area | Required cases and observations |
| --- | --- |
| Primary access | Hit/miss lookups; random/sequential keys; small/large records; selective projections and nested fields. |
| Mutations and transactions | Insert/update/delete/upsert, read-modify-write, mixed workloads, single and batched commits, conflict/abort behavior, unique/relationship constraints. |
| Indexed queries | Equality, short ranges, composite/covering/partial/multikey access, ordering/pagination, skew/hot keys, first queries and workload shifts. |
| Broader supported operators | Scans, joins, sorting, aggregation, full-text, and spill behavior whenever advertised. |
| Sustained operation | Checkpointing, compaction, index builds, concurrent backups, long snapshots, space reclamation, and capacity pressure during foreground work. |
| Resource scaling | Idle and many-database overhead within the fixed 4-vCPU/8-GiB Firecracker budget; datasets beyond RAM and SSD cache; bytes and allocations/copies per operation. |
| Physical strategies and tiers | Bf-Tree-inspired, ZoneTree-inspired, and combined layouts; SSD/S3 placement; workload shifts; transition overhead; ordered scans and transactions crossing layouts. |
| Startup and recovery | Empty and populated opens, dirty restart/WAL replay, cold object-backed open, SSD loss, backup restore, and time to useful query service. |
| Replication and elasticity | Local/LAN/WAN profiles, durable/apply lag, throughput under latency/loss, slow/offline peers, partition healing, bootstrap, repair, and shard movement. |
| Storage economics | Local/remote bytes, read/write amplification, S3 request counts and transfer, per-tier cache hit ratio, SSD wear/write volume, and cost per workload/GiB-month. |

Record throughput, p50/p95/p99 and relevant p99.9 latency, CPU, peak/steady RSS/PSS/container memory, allocator live/retained bytes, disk/remote I/O, storage amplification, replication/index lag, startup/recovery time, and total infrastructure/model costs. Report client, database, RustFS, and advisor resources separately and as a whole deployment.

Keep all benchmark drivers, seeds/data generators, dataset definitions, configuration, environment limits, hardware, toolchain/dependency versions, source revisions, raw samples, profiles, and summary reports in `../ts-benchmark`. Do not save benchmark files or results in this repository. Record the benchmark workspace revision as well as the engine's. Run repeated trials and publish variance/confidence intervals and a predefined practical significance threshold.

Maintain a per-case SQLite scoreboard: win, loss, statistically inconclusive, unsupported, or not comparable with the reason. The final performance objective requires a demonstrated win in every applicable declared performance case and satisfaction of all resource/correctness gates. Unsupported or inconclusive required cases remain open; a geometric mean or a marketing summary cannot mark them complete.

Use focused regression gates during development and scheduled controlled full-matrix runs. Shared CI machines may detect gross regressions but cannot certify small performance wins. Update targets only through an explicit documented scope decision with all benchmark results retained.

## 11. Correctness, backup, and operational qualification

Run correctness qualification in the Firecracker environment specified in section 10. Maintain differential tests against SQLite for shared transaction/query semantics, compare scans with indexed results, and verify data before and after recovery. Use independent reference models for distributed semantics SQLite cannot represent. Add property tests for binary formats, ordering, merges, and recovery; deterministic scheduling/simulation for replication and transaction failures; fuzzing for hostile/corrupt input; and process-level crash campaigns.

Test torn/truncated/corrupt writes, abrupt termination at durability boundaries, partial acknowledgments, disk full, failed sync, failed uploads/publication, partitions, duplicate/reordered messages, clock skew, stale membership, and restart during compaction, physical-layout transitions, tier movement, index activation, GC, transfer, and restore. Check acknowledged-data survival, atomic visibility, constraints, bounded resources, convergence, and absence of resurrection. Differentially verify identical logical results across qualified physical strategies and snapshots spanning their transitions.

Provide native S3 backups with consistent snapshots, incremental log archival, point-in-time recovery, encryption, resumable uploads, retention, and safe GC. Backups must preserve schemas, index definitions/decisions, format versions, transaction/causal metadata, and consistent cross-shard restore points supported by the declared protocol. Do not include plaintext credentials. A live object tier is not a substitute for independent retained backups.

Automate clean-environment restores against RustFS and qualified production providers. Verify data, constraints, indexes, lineage, and resumable jobs. Publish measured recovery-point/recovery-time results. A backup is accepted only after restoration succeeds; an archived frontier is not a validated restore.

Provide typed query/mutation contracts, authorization, dependency-tracked subscriptions, consistent snapshot-plus-delta synchronization, and resumable cursors. Share subscription work only within compatible authorization scopes. Bound slow-consumer queues and trigger resynchronization when retention is exceeded. Keep CDN-cacheable delivery tenant/auth safe.

Keep external side effects outside retried database transactions; use durable outbox/scheduling and idempotency. Expose readiness, health, capacity/admission failures, lag, degraded cold-cache performance, and cost diagnostics. Test graceful shutdown, upgrades, credentials/certificate rotation, and supported architectures through real callers.

## 12. Delivery sequence and completion evidence

Deliver executable increments with source, tests, performance receipts, diagnostics, and updated architecture/operation documentation:

1. **Baseline and Rust foundation.** Provision the reproducible Firecracker harness with 4 vCPUs and 8 GiB RAM per microVM. Establish fair SQLite drivers, the versioned workload matrix, the Rust workspace, memory accounting, a working RustFS fixture, and a hosted Von fixture with successful warm-up inference. Implement an initial durable Rust point/range/transaction path and measure it end to end within the fixed limits.
2. **Memory-bounded SSD/S3 Rust engine.** Benchmark Bf-Tree-inspired and ZoneTree-inspired components and qualify the best physical strategies per workload and SSD/S3 tier, including combinations supported by evidence. Deliver checksummed formats, durable logging, transactions, remote publication, bounded caches/spill, cold open, safe layout/tier transitions, and crash/device-loss recovery for declared profiles. Preserve an extension boundary for a future HDD tier without making its implementation a milestone requirement.
3. **Initial and adaptive indexing.** Deliver deterministic initial indexing, optional production Von/JEV with one durable decision per generation, mature physical indexes, and the measured workload lifecycle. Require live tests against the hosted Von service with accepted model advice, consistency/recovery checks, and held-out comparative benchmarks in `../ts-benchmark`.
4. **Live Anna-informed replication.** Connect leaderless merge semantics to gRPC streaming, durable cursors, anti-entropy, bootstrap, failure-domain acknowledgments, and necessary coordinated invariants. Qualify partitions, lagging peers, rebalancing, and resource limits.
5. **Product integration and operations.** Connect schemas, relationships, typed APIs, reactive delivery, backups/PITR, and operations to the real Rust engine. Qualify supported rolling format/protocol upgrades and clean restores.
6. **Competitive qualification.** Close every required correctness and SQLite scoreboard gap, validate sustained operation and cost on the declared topology, and publish the full reproducible acceptance report.

Memory limits, durability, recovery, and benchmarks are required throughout these increments. Do not postpone them until after feature completion.

Completion requires working runtime integration and evidence for the actual supported configuration. Plans, stubs, isolated algorithms, passing mocks, and paper results do not establish completion.

## 13. Primary references and verification

Use original papers for mechanisms and official documentation/source for APIs. Verify assumptions, publication identity, supported versions, and artifact availability when adopting a mechanism. Record what was adopted, what differs, and which test/benchmark qualifies it.

| Source | Intended use |
| --- | --- |
| [Anna: A KVS for Any Scale](https://dsf.berkeley.edu/jmh/papers/anna_ieee18.pdf), ICDE 2018 | Shared-nothing execution, lattice composition, and asynchronous leaderless replication. |
| [Autoscaling Tiered Cloud Storage in Anna](https://www.vldb.org/pvldb/vol12/p624-wu.pdf), PVLDB 2019 | Selective replication, tier placement, and elasticity; SSD/S3 adaptation needs independent evidence. |
| [Anna source](https://github.com/hydro-project/anna) | Mechanism and experimental-artifact inspection; no inherited TerraBase performance claim. |
| [Schema-Agnostic Indexing with Azure DocumentDB](https://www.vldb.org/pvldb/vol8/p1668-shukla.pdf), PVLDB 2015 | Initial document-path coverage and exclusions without query history. |
| [AIM: A practical approach to automated index management for SQL databases](https://arxiv.org/abs/2605.31406) | Practical recommend/build/validate/monitor/retire lifecycle; verify the original publication separately from the repost. |
| [Bf-Tree paper](https://www.vldb.org/pvldb/vol17/p3442-hao.pdf), PVLDB 2024, and [source](https://github.com/microsoft/bf-tree) | Required inspiration for TerraBase-owned Rust mini-page caching, buffered updates, and ordered access; benchmark against LSM-inspired and combined strategies with durability enabled. |
| [ZoneTree architecture, source, and benchmarks](https://github.com/ZoneTree/ZoneTree) | Required inspiration for TerraBase-owned Rust LSM segments, lookup/cache efficiency, merges, logging, transactions, and compression; select mechanisms by measured workload and SSD/S3 behavior. |
| [DBSP](https://www.vldb.org/pvldb/vol16/p1601-budiu.pdf), PVLDB 2023 | Incremental maintenance for supported reactive queries. |
| [Tonic](https://github.com/hyperium/tonic), [Prost](https://github.com/tokio-rs/prost), [Tokio](https://docs.rs/tokio/) | Rust runtime, generated gRPC contracts, streaming, flow control, and cancellation. |
| [gRPC compression](https://grpc.io/docs/guides/compression/) and [HTTP/2 protocol](https://github.com/grpc/grpc/blob/master/doc/PROTOCOL-HTTP2.md) | Framing, limits, encoding negotiation, and interoperability. |
| [object_store](https://docs.rs/object_store/), [AWS SDK for Rust](https://docs.aws.amazon.com/sdk-for-rust/latest/dg/welcome.html), [S3 consistency](https://docs.aws.amazon.com/AmazonS3/latest/userguide/Welcome.html#ConsistencyModel) | Version-specific object APIs and provider guarantees. |
| [RustFS](https://github.com/rustfs/rustfs) and [S3 compatibility matrix](https://github.com/rustfs/rustfs/blob/main/docs/architecture/s3-compatibility-matrix.md) | Required local/CI S3 integration fixture and capability qualification. |
| [lz4_flex](https://github.com/PSeitz/lz4_flex) | Rust LZ4 implementation and explicit format compatibility. |
| [Von](https://github.com/wfzyx/von), [JEV/System One](https://docs.typesafe.ai/concepts/system-one), and [HTTP API](https://docs.typesafe.ai/api) | Bounded initial advice with verified request/response and model versions. |
| [SQLite WAL](https://www.sqlite.org/wal.html), [PRAGMA reference](https://www.sqlite.org/pragma.html), and [JSON/JSONB](https://www.sqlite.org/json1.html) | Fair baseline tuning, durability, and document-query semantics. |
| [Firecracker](https://github.com/firecracker-microvm/firecracker) and [getting started](https://github.com/firecracker-microvm/firecracker/blob/main/docs/getting-started.md) | Reproducible test and benchmark microVMs with enforced 4-vCPU and 8-GiB guest limits. |

Keep architecture and operational documentation synchronized with the Rust implementation. Attach dated evidence to the exact source revision and configuration it qualifies. This goal defines the target architecture; completion requires the implementation and acceptance evidence specified above.
