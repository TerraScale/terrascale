# ts-dotnet goal

## Mission

Deliver the official C#/.NET 10 customer SDK with typed, asynchronous public
HTTP/reactive operations. C# does not own a TerraScale server or gateway.

## Authority and work rules

The [workspace goal](../GOAL.md) defines product scope and shared acceptance.
This repository goal restates its applicable responsibilities. Operational
integration follows the GRU master, regional authority, same-host management,
and qualification requirements reconciled from
[`ts-orchestrator/GOAL.md`](../ts-orchestrator/GOAL.md). Goals contain decisions
and acceptance criteria; implementation findings and feature proposals belong
in review documents, not progress claims here.

Work on `develop` in one checkout. Use `fj` for Forgejo. Never edit human-owned
`AGENTS.md` or `CLAUDE.md`, add co-author trailers, or include unrelated work in a
commit. The owner's explicit commit request means commit and push before
running tests, checks, or builds; verify afterward. Release qualification still
requires the appropriate green gates. Run Rust commands at lowered priority.

## Shared product contract

- This is the first public release. Use TerraBase and the coordinated v1 names;
  the public API is `/api/v1`, sync is `terrascale.sync.v1`, and MessagePack is
  1.0. Retired pre-launch designs need no compatibility layer. Published public
  contracts follow the workspace compatibility commitment.
- TerraBase's Rust member owns the engine and public database/reactive API.
  SQLite is the only storage engine; SQL is read-only, while writes are named
  document operations. A separate gateway, TQL, vector/spatial support,
  PostgreSQL compatibility, and a shipped local engine are outside launch.
- Only writes and logical storage are billed, including their declared Region
  multiplier. Reads, egress, subscribers, delivery, replay, TTL expiry, and
  webhook delivery stay unbilled within published guards. TerraBase owns usage
  truth; no other service calculates a competing usage ledger.
- Launch uses GRU/SFO/FRA on OCI plus Cloudflare. Every member host has local
  orchestrator and `ts-node` services. The permanent global control R3 stays in
  GRU/SFO/FRA when the estate grows to 10 or 20 Regions. Customer data and
  replication never pass through NATS or the orchestrator.
- Placement and residency are immutable per database. Estate size does not
  determine a database's replication set. Honor the same lock for routing,
  replication, backup, restore, export, branches, and repair.

## Public client behavior

- Expose named typed CRUD, patch, delete, bounded bulk, tenants, database
  management, schema, history, TTL, FTS, backup/PITR/branch, import/export,
  usage, and webhook operations only after their owning public contract exists.
  Preserve typed contract-gap errors while a required operation is missing;
  such errors are not completion of the launch feature.
- Generate DTOs, operation metadata, and wire artifacts from an explicit,
  immutable TerraBase contract bundle. Pin artifact digests as well as the
  version name. Validate route, method, requiredness, media type, auth, errors,
  retry, idempotency, pagination, and checkpoint parity. Publish no invented
  route or generic request escape hatch.
- Ship schema builders that emit `terrascale.schema.json`, generated types, and
  typed query builders producing parameterized SQLite-dialect SQL. Raw
  parameterized SQL remains available. Live SELECT supports a single collection,
  filters, ordering, LIMIT, and FTS; execute-only constructs return the specified
  `NotSubscribable` error when subscribed.
- First-contact REST uses `api.terrascale.tech`; subsequent reads and writes pin
  to the selected regional member origin. Preserve sessions and idempotency
  through bounded retries and `421` placement repair. Never widen residency.
- View attach runs on the member. Reactive delivery defaults to bounded HTTP
  polling on `sync.api.terrascale.tech`, with opaque checkpoints, immutable
  snapshots/logs, and a capability header. The Snippet gates CDN access; no
  Worker handles delivery. WebSocket is opt-in and SSE is outside launch.
- Cache only authorized result subsets. Apply snapshots, deltas, moves, resets,
  duplicates, and replay atomically. Authorization changes must remove revoked
  results before exposing replacements. Bound memory, concurrency, and buffers.
- Optimism is reversible presentation around an online server mutation with one
  stable idempotency identity. No durable offline writes, local CRDT actors,
  whole-database replicas, or persistent result caches at launch.
- Carry cancellation, deadlines, safe request IDs, session/placement metadata,
  `Retry-After`, and actionable typed errors. Never retry permanent validation,
  auth, authorization, or deterministic conflict errors. Do not log credentials,
  capabilities, arbitrary headers, or customer payloads.
- Enforce the public 1 MiB document limit and contract bounds without losing
  64-bit identifiers. Keep canonical document JSON opaque until the language's
  delivery boundary. Metering, replication, key ownership, and authorization
  policy remain on the server.

## Integration acceptance

The same producer conformance corpus qualifies every SDK: success and denial,
unknown wire versions, hostile sizes and integers, Unicode, partial outcomes,
idempotent retry, pagination, session reads, Region movement, permission change,
slow consumers, reconnect, and expired history. Compare immutable byte vectors
and semantic outcomes across all four languages. A self-generated round trip
alone does not prove conformance.

Qualify a packaged SDK against the real member API and the shared three-Region
journey. A local contract directory is explicit pre-publication input, never an
implicit sibling dependency. Published artifacts must work in a clean checkout.
Report unavailable live configuration as skipped. Portable checks, browser
checks where relevant, the Firecracker host gate, and real-provider evidence
remain distinct.

## .NET responsibilities and verification

Use `HttpClient`, source-generated `System.Text.Json`, nullable analysis,
immutable models, DI integration, and `CancellationToken`. Never dispose a
caller-owned client or block with `.Result`/`.Wait()`. Use bounded
`IAsyncEnumerable<T>` streams and explicit reactive lifetime ownership.

Schema attributes/builders and generated bindings produce the launch schema
JSON and parameterized SQL. Framework-specific integrations must use this SDK
and public operations. They cannot resurrect removed environment/repository
roles or private server references; launch framework adapters remain the
TypeScript responsibilities specified by D-13.

`make check` obtains the explicit pinned bundle, regenerates and compares
contract output, verifies format, builds Release, and runs TUnit. Exercise
success and failure for the generated MessagePack envelope and the handwritten
HTTP decoder. Keep live product tests environment-gated and label skips.
