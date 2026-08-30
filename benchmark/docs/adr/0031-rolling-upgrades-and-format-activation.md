---
status: accepted
date: 2026-08-29
authority: ../../../GOAL.md
---

# Rolling upgrades and format activation

## Release identity and support

Every binary, container, generated binding, schema, native codec, storage
format, and migration names an immutable source commit, artifact digest, release
ULID, semantic version, and required feature set. Nodes refuse an artifact whose
signature, digest, environment, architecture, or admitted release identity does
not match the Orchestrator release decision.

Private TerraBase components support only the current release `N` and previous
release `N-1` during a bounded rolling upgrade. Mixed private releases may run
for at most twenty-four hours. They negotiate the intersection of optional
features and reject unknown required behavior. This window is operational
compatibility, not a permanent compatibility reader.

## Surge-member rollout

RF=2 cannot take one Member offline and still satisfy `RegionalDurable`.
Therefore every production Member upgrade uses temporary surge capacity:

1. validate signed artifacts, free space, quotas, backups, rollback state, and
   protocol compatibility;
2. allocate a temporary Member in a different OCI fault domain or availability
   domain from the seat it protects;
3. bootstrap and Merkle-verify the surge Member at the required frontier;
4. admit it through joint consensus without weakening current acknowledgements;
5. upgrade or replace one old Member, catch it up, and requalify reads, writes,
   repair, and resource isolation;
6. repeat for the second old Member;
7. activate any gated format or feature only after every required reader is
   compatible; and
8. remove surge capacity through joint consensus after a stable observation
   window.

At least two qualified compatible active Members remain in every writable
Region. Single-Region databases receive the same surge procedure. An upgrade
never creates planned write downtime or excludes maintenance from the
availability SLO.

The Orchestrator first upgrades internal system databases, then a bounded canary
tenant cohort, one production Region at a time, and finally the remaining
cohorts. Error, latency, recovery, replication, sync, resource, and authorization
guardrails pause or automatically roll back a wave before format activation.

## Expand, activate, contract

An additive release first expands readers and writers while continuing to emit
the old active format. After every voter, backup consumer, gateway, SDK where
applicable, and recovery tool proves support, one coordinated activation record
selects the new feature or format for new writes.

The current binary retains an older persisted-format reader until every
retained WAL, segment, snapshot, backup, PITR, and audit pin using that format
is rewritten or expires. This does not extend old binary or private-protocol
support beyond N/N-1. Only one incompatible persisted major may be retiring at
a time. A durable ADR 0028 operation performs rewrites and records their
verified frontier. Contract removes old write behavior only after no admitted
old writer remains.

Protobuf field and enum identifiers, datatype codes, storage codes, and
canonical meanings are never reused. Unknown required behavior fails closed
instead of being preserved as an opaque value.

## Rollback boundary

Before activation, the Orchestrator may restore the previous signed binary and
configuration after it fences the failed release. Once a coordinated record
activates a new persisted major or irreversible migration, binary downgrade is
forbidden. Recovery must roll forward with a corrected compatible release or
restore a new cluster from a verified pre-activation backup. It never makes an
old binary interpret new bytes.

Every release declares:

- the last reversible stage;
- compatible private and public versions;
- schema, codec, and storage activation records;
- surge capacity and disk requirements;
- rollback and roll-forward commands;
- backup and restore prerequisites;
- irreversible operations; and
- evidence required before the next wave.

## Public contract evolution

Public REST and reactive compatibility follows ADR 0032. A private rollout
cannot remove an admitted public version before its support window ends. The
Database API translates each supported public major to one typed private TBEP
operation set; it does not forward generic old payloads into the engine.

## Evidence

Qualification covers every supported N/N-1 pairing, both upgrade directions,
mixed Rust and Go bindings, canary halt, process and Region failure at each stage,
surge-member loss, joint membership, WAL and snapshot recovery, backup restore,
key rotation, delayed clients, public-version overlap, pre-activation rollback,
post-activation roll-forward, and twenty-four-hour mixed-version expiry.

An Upgrade Receipt records exact artifacts, signatures, versions, feature sets,
Members, Region airport codes, frontiers, format activations, guardrails,
rollback boundary, operation ULID, and final verdict.
