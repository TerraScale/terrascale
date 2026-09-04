# ts-docs goal

## Mission

Publish accurate TerraScale documentation, runnable examples, the Next
starter, and architecture explanations that match the public v1 contracts.

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

## Documentation and developer experience

Explain document writes and read-only SQL, typed schema/query builders, FTS,
TTL/history, live SELECT, optimistic presentation, opaque checkpoints, typed
resets, regional pinning, immutable reactive HTTP, CRDT replication, durability,
backup/PITR/branches, imports/exports, and the published limits. Document real
supported workflows and clearly identify unqualified or missing features.

Every published snippet must run against an authorized real sandbox branch
with explicit credentials. Stub tests are feedback on example structure; they
cannot qualify the public contract. Keep keys out of source/build output and
avoid third-party analytics leakage from credential-bearing examples.

`create-terrascale-app` provides the launch Next starter. Its sign-in, cloud dev
branch, seed, schema/codegen, and two-client live update journey must work on a
fresh machine in under five minutes. Ship useful assistant instructions in the
customer template; human-owned workspace instructions are not editable here.

Publish writes/storage pricing with Region multipliers and the shared branch
allowance. State that reads/delivery are unbilled within guards, not that
infrastructure costs nothing. Provider prices/terms require current measurement
and owner acceptance. Keep legal, privacy, DPA, status/support links, deletion,
Free pause/reap notices, and compatibility commitments accurate.

## Architecture and review artifacts

Keep editable Markdown/Mermaid request-flow diagrams and rendered pictures for
3, 10, and 20 Regions. Distinguish the six-host launch topology from expansion
illustrations. Show REST first contact then direct regional requests, Snippet
and cache delivery, SQLite/journal/view internals, physical RF=2, QUIC causal
replication, regional object storage, same-host orchestrators, and GRU control
authority with permanent global R3. Additional Region labels are illustrative
until the owner chooses locations.

State explicitly that estate growth does not widen a database's placement.
NATS is control state only, Grafana owns collected telemetry, and TerraBase owns
usage facts. Rendered artifacts must be inspectable and reproducible. Keep
internal dated reviews and proposed features outside public site navigation;
they are not normative goals or release-readiness claims.

## Completion gates

Run `make check`, site build, link/contract checks, and snippet/starter tests
appropriate to the change. Label real-sandbox skips and preserve runnable
commands. Architectural diagrams require visual inspection as well as valid
source. A site build alone cannot certify backend, billing, auth, replication,
or Cloudflare performance.
