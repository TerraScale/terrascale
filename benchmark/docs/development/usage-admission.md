# Private usage registration

`ProductionAdmissionRuntime` opens `usage-admissions.sqlite` under its existing
Member root. An accepted database remains closed until a durable registration
matches its owner, member, Region, selected Regions, residency, writer epoch,
and accepted placement proof. Missing registration is pending control delivery;
it does not prevent the private admission listener from starting. A mismatch
or corrupt row refuses activation.

The receiver is `POST /internal/v1/databases/usage/admit` on the existing Unix
admission socket. The listener checks the configured orchestrator UID before
dispatching HTTP. This route is absent from the public API. JSON bodies are
limited to 16 KiB, unknown fields are refused, and at most 16 registration
operations may execute or wait for blocking work.

| Request field | Meaning |
| --- | --- |
| `schema_version` | Exactly `1` |
| `environment` | Exact local environment |
| `tenant_id`, `project_id`, `database_id` | Canonical ULID owner identities |
| `member_id`, `region` | Exact receiving member and local origin |
| `selected_regions` | Sorted unique full database selection, including the local Region |
| `residency` | Immutable named policy from database admission |
| `partition` | Nonempty lifecycle-authorized partition, at most 255 bytes |
| `stream_epoch` | Positive unsigned accepted writer epoch |
| `placement_proof_digest` | Lowercase 64-character SHA-256 digest of the signed accepted proof |
| `operation_id` | Stable control retry identity, at most 256 bytes |

The receiver verifies retained signed acceptance, ties it to the same proposal
revision, and checks exact database assignments and the local physical seat.
Proposal intent and cluster-wide Region membership cannot grant a stream.
The authenticated orchestrator supplies the billing partition; the engine never
derives one from a database name or ID.

A successful response repeats every request field and adds `state: registered`.
That acknowledgment follows a SQLite transaction with FULL synchronization.
It proves registration persistence, not database readiness. Retries retain the
same operation and body. Changed retries within an epoch are conflicts.

The journal permanently binds its environment/member/Region. Database owner,
selected Regions, residency, and partition remain immutable across epochs.
New epochs require an independently accepted signed transition, whose chain
verifies predecessor fencing. Fixed-width decimal keys preserve the full
unsigned epoch range. Earlier rows remain retained; stale registration attempts
cannot replace the latest epoch. Deletion uses the engine's durable lifecycle
fence; retained registration metadata never revives a writer.

The root, SQLite file, and sidecars must be private to their owner. Symlinks,
hard-linked files, and files accessible to other users are refused. Reads bound
the serialized payload and validate it against its database/epoch key.

## Remaining integration and qualification

The Go orchestrator now reads exact active streams from its durable lifecycle
journal and delivers them to both regional members after signed placement
acceptance, before recording activation completion. The planner includes retained
live databases affected by the new cluster epoch. Deleted scopes receive no new
registration. Normal creation still lacks an authoritative stream allocator;
missing streams keep activation pending.

The daemon entrypoint, signed successor epoch test, and pending-registration
recovery through the complete production runtime remain unqualified. Go transport
tests use authenticated NATS and a Unix HTTP fake engine. These focused tests do
not pass the production launch or billing release gates.
