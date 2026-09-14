# ADR 0048: Shard replication lanes and authenticated split admission

Status: Proposed; requires integrated producer and consumer implementation before activation.

## Context

An online split creates another durable storage file, not another logical database.
`DatabaseRouteScope` currently identifies only tenant/project/database.
`WalAssignment`, `WalReplicaBinding`, WAL envelopes and snapshots, causal
bindings, and production peer progress consequently cannot distinguish the
source file from its child. `build_peer_databases` rejects repeated database
scopes. A source RF2 acknowledgement cannot establish child durability.

`PlacementDatabaseAssignment` binds one writer/follower per database and Region.
The orchestrator's accepted placement proof binds scope, epoch, generation and
members, but no split, incarnation, range, or child file. The regional autoscale
controller consumes CPU/memory/queue observations and sets member counts; it
does not produce a shard split candidate. `MemberSplitRuntime` has no production
constructor caller. These are integration prerequisites, not authority that can
be inferred from a parent grant or from healthy processes.

## Proposed decision

Introduce `ShardLaneIdentity { scope: DatabaseRouteScope,
database_incarnation: u128, shard_id: u64 }` in `terrabase-cluster` and use it at
every physical and causal data-plane lookup. Incarnation and shard identifiers
must be nonzero, matching the engine's route admission codec. The persisted engine file identity is
the authority for its incarnation/shard, including the initial file. A caller
must never fabricate an initial identity when durable identity is absent.
Public database IDs remain unchanged. Internal envelopes change directly;
there is no fallback accepting a database-only envelope for a child.

Originating causal streams additionally bind ADR 0022's immutable
`Actor=(ReplicaUlid, ReplicaEpoch)` and Region. Do not introduce an unrelated
origin-incarnation counter or derive an actor from a process or host identity.
Physical followers share the file lane without becoming a new originating
actor. ADR 0049 defines original operation results and target materialization
provenance; source outcomes retain their original actor and sequence.

The logical public `BoundScope` is retained for authorization. A separate
internal `BoundShardScope` carries the lane and authenticated peer principal;
it cannot be constructed merely by accepting caller-supplied shard fields.
Do not add shard semantics to unrelated customer backbone channels.

### Signed split records

Use exact signed JSON bytes and Ed25519, with domain
`terrabase-shard-transition-v1\0`; never reserialize bytes to verify signatures.
The wire wrapper contains `key_id`, `payload`, `signature`. Hashes below mean
SHA-256 of exact payload or artifact bytes, encoded as lowercase hex. The
candidate, grant and each receipt use distinct `kind` values and cannot be
substituted for each other. Unknown fields/versions are rejected. Identifiers
representing u128 use exactly 32 lowercase hex digits; ULIDs remain canonical
ULIDs. Key bounds use base64 bytes; null means unbounded and differs from empty.

The grant is `SplitGrantV1` with these fields:

| Field | Binding |
| --- | --- |
| `schema_version`, `kind` | `1`, `admitted_split` |
| `operation_id`, `split_id` | Stable control operation and unique split identity |
| `scope`, `database_incarnation` | Immutable tenant/project/database and current incarnation |
| `source_shard`, `target_shard` | Distinct persisted file identities |
| `source_lower`, `source_upper`, `split_key` | Exact source interval and interior key; target owns `[source_lower, split_key)`, source retains `[split_key, source_upper)` as in `RouteTable` |
| `expected_route_generation` | Exact route CAS predecessor |
| `placement_epoch`, `placement_generation`, `parent_proof_digest` | Current accepted database placement and immutable residency authority |
| `candidate_digest` | Exact authenticated member candidate payload |
| `target_assignments` | Exactly one writer and one different follower per selected Region, sorted by Region |
| `authority_epoch`, `journal_revision` | Fenced GRU authority and durable issuance revision |

Each target assignment binds the complete target lane, Region, exact writer and
follower `MemberWorkloadIdentity`, receipt key IDs and public-key digests. A
control-plane resolver supplies keys from durable enrollment. Receipt-supplied
keys and endpoints never become authority. All selected Regions are inherited
from immutable database residency; splitting cannot widen it.

`SplitCandidateV1` is signed by the enrolled source writer. It contains scope,
incarnation, source shard, exact current range, proposed split key, route
generation, placement epoch/generation/proof digest, observation sequence,
observed time, source workload identity and measured trigger facts (size,
sustained write rate or latency window). The producer reads the current engine
catalog and bounded monitor. The orchestrator revalidates writer enrollment,
freshness, placement, route and target capacity. A member suggestion does not
elect target members or grant admission. Split IDs are allocated and durably
reserved by GRU before issuance; a retry finds the same operation.

### Distinct baseline and serving evidence

The grant authorizes preparation, not customer serving. Baseline bytes do not
exist when a candidate is evaluated and therefore cannot be invented in the
grant. The subsequent `TargetReadyReceiptV1` binds `grant_digest`, signer lane
and workload, immutable baseline format/version and artifact digest, plus:

- The source frozen cut: source lane, session epoch and publication sequence.
- Retained-history floor and the per-origin causal frontier.
- Final replay-through cursor in source publication units.
- Target publication sequence in target-local units.
- Physical follower checkpoint evidence for that exact target file, epoch and
  baseline, including whether zero was actually observed.

These units stay distinct typed fields. A maximum sequence or an empty vector
is not a proof of all of them. Baseline hashing covers retained state/history,
causal state, schema/control state and retained operation/request outcomes,
including original origin/sequence provenance needed by GlobalDurable retry
confirmation. Source local sequence cannot replace that provenance or
become a target public document revision. The baseline format is owned by the
engine snapshot/importer contract and must be finalized with that owner.

`SplitServingReceiptV1` binds grant digest, baseline digest, target-ready receipt
digests, complete lane assignments, the fenced final source cut, and committed
route generation. The source cutover transaction and target runtime produce
the facts; GRU records/publicizes them rather than manufacturing engine proof.
Every selected Region must supply the required bound target durability facts
before global serving qualification. A source database readiness receipt is
never reusable as target readiness.

### Durable lifecycle and failure behavior

Keep a bounded authorityjournal operation record and per-source active head:
`Planned -> Granted -> Ready -> Serving`, with `Aborted` only before durable
cutover. Reserve split ID, target lane and predecessor route generation.
Identical candidate/plan retries return the original signed grant; changed
bindings under any reserved identity conflict. Durable issuance precedes
delivery, and all deliveries/receipts are replayable by operation identity.
Readiness cannot remove source data. Receipt publication after a lost response
must recover the engine's durable route receipt before retrying or aborting.

Loss of GRU/quorum refuses new grants. Previously admitted data service retains
its existing outage guarantees; temporary authority unavailability is distinct
from a proved revoked or mismatched assignment. Post-cutover recovery never
rolls back routing by deleting the target.

## Integration and acceptance

1. Propagate the lane through WAL assignment/source/binding, batch and ack
   formats, snapshot addressing, physical/causal dispatch, peer registry,
   progress and durability waits. Use target file handles, not source aliases.
2. Implement retained baseline import and a budgeted Member target registry.
   Worker, public serving and replication drivers share the same admitted
   engine handle. Restart, backup, restore, draining and deletion include it.
3. Publish producer-owned `ShardControl` candidate/admission/status contracts;
   authenticate them with the existing private workload transport. Implement
   candidate production, accepted-grant persistence and verification in the
   actual server before introducing the orchestrator issuer.
4. Add orchestrator `shardtransition` and `productionShardTransition` together:
   production reconcile polls current authenticated candidates, selects
   admitted target seats, durably issues, delivers to replicas and source,
   reconciles receipts and resumes operations after restart. Capacity scaling
   can provision missing seats through the existing target path; an integer
   scale-out target alone is never split authorization.
5. Connect point and database-wide consumers, sessions, batch atomicity, SQL,
   list/export/history, reactive publication and usage ownership before route
   publication. Keep production split activation closed until these callers
   and complete receipt validation exist.

Required verification covers cross-file receipt substitution, stale
incarnation/epoch/route, wrong workload/Region, unobserved empty target,
baseline mismatch, retained GlobalDurable retries, lost admission/cutover
responses, restarts, authority outages, RF2 follower failures, and actual public
reads/writes across both ranges. No build or unit result alone establishes
production readiness.
