# Ready brief: routed SQL failure closure

Status: ready for coordinator dispatch; source and tests are read-only for this
preparation lane. The existing routed SQL implementation is present, but its
public serving contract is not closed.

## Outcome and current gap

`crates/terrabase-server/src/api.rs::execute_sql` is the production caller. It
opens a session/read snapshot from the source engine, then separately asks
`host.engine_host().admitted_shard_read_cut()` and calls
`QueryStore::execute_admitted`. The query layer materializes all referenced
relations from each participant into one bounded SQLite evaluator, which is the
correct shape for joins, aggregates, grouping, ordering, and LIMIT
(`src/query/sql_execute.rs:253-410`). Existing unit coverage proves patched
target visibility, stale source exclusion, aggregate/join/full-text merge, and
page-sized materialization (`src/member/shard_registry.rs:476-595`).

The remaining failure boundary is admission/serving: `read_cut` validates the
route generation only while capturing snapshots (`src/member/shard_registry.rs:95-140`),
but the API does not revalidate it before or after the SQL pass. A cutover can
therefore produce a successful response from a generation that is no longer
current. The session snapshot opened before the cut is also not the snapshot
used by routed execution; `execute_admitted` consumes only the cut
(`src/query/mod.rs:681-704`). Session frontier coverage across every selected
lane is not checked at this boundary. These are confirmed source gaps; whether
the existing session token contract already supplies the needed multi-lane
frontier is an architecture decision for Astra if the worker cannot establish
it from existing APIs.

## Ownership and dependencies

* Serving owner writes `src/member/shard_registry.rs`, `src/member/mod.rs`, and
  the private admission-to-query adapter only if the current cut API cannot
  carry the required validation. The API owner writes
  `crates/terrabase-server/src/api.rs` and its real caller-path tests.
* Query owner writes `src/query/mod.rs`, `src/query/routed.rs`, and
  `src/query/sql_execute.rs` only for a narrowly required error/cut contract;
  do not duplicate the evaluator or reparsing/binding rules.
* Tests are writes: the serving/API owner owns routed SQL integration fixtures;
  the query owner owns focused evaluator tests. No owner edits another lane's
  files or generated contracts without a handoff.
* Read-only dependencies are `AdmittedShardReadCut`,
  `Engine::routed_read_snapshots`, `RouteParticipantRegistry::is_current`,
  `Session::read_snapshot`, `SessionToken`/frontier checks, and the existing
  `session_error` mapping. `engine.proto`/OpenAPI changes are forbidden unless
  Astra explicitly decides a public error or token contract change is needed.
* Shared resource: use the existing lowest-priority `cargo test`/`make check`
  build owner and hand off selectors/log paths; do not start a competing full
  build. The focused check is currently blocked before test execution by an
  unrelated dirty source compile error in
  `src/engine/branch_restore.rs:35-37` (`lock_path` moved into `OpenOptions::open`
  then borrowed by `map_err`). Preserve that evidence; do not fix it in this
  routed SQL lane.

## Bounded design

Keep one immutable routed cut as the authority: authenticated tenant/database
identity, route generation, validated participant identities/ranges, and one
snapshot per participant. Add a serving-owned generation check immediately
before cut capture (the existing `routed_read_snapshots` check is the natural
seam) and a second check after `execute_admitted` and before rendering. A
changed generation returns the existing conflict/retry response; it never
returns an empty/source-only result. The post-check must retain the cut's
admission lifetime until it completes.

The routed query evaluator remains one SQLite pass over route-filtered raw
relations. For every emitted document, its canonical key must belong to exactly
one captured route, come from that participant snapshot, and match the
authenticated tenant/database incarnation. Preserve input/output byte and row
bounds, deadline/cancellation, VM progress, rollback, parameter binding,
latest-version/deleted/expiry semantics, and the existing causal (not global
linearizable) snapshot claim.

Before implementation, the worker must trace the current `Session` API and show
how a supplied token is checked against every participant lane. If the session
contract cannot express that check, stop at the private cut boundary and send
Astra the exact missing interface and consequence. Do not invent a token
version, public response shape, or stronger ordering promise.

## Acceptance cases

1. Through the real server fixture, seed source and target records with the
   same ID, perform an accepted split/cutover, write the moved record through
   the normal document API, and POST SQL. The result contains the target value
   exactly once and never the stale source value.
2. Run a two-collection aggregate/join/group/order query spanning both route
   intervals; it returns one globally evaluated result with existing SQL row,
   byte, deadline, and cancellation behavior.
3. Change the route generation after cut admission but before return (a
   deterministic test hook or existing fixture mechanism). The SQL request
   returns conflict/retry and never renders a mixed-generation result.
4. Supply a stale or incomplete session frontier. The request returns the
   existing explicit session coverage/stale-replica refusal, not stale data,
   an empty result, or a source fallback. No-session requests use current
   local cuts and do not manufacture a global session token.
5. A route gap, missing target, duplicate participant, identity mismatch, or
   tenant mismatch refuses closed with the existing storage/conflict mapping;
   no partial evaluator result is returned.

The focused command is `nice -n 19 cargo test -p terrabase --lib
member::shard_registry::tests::routed_sql -- --nocapture`, followed by the
server fixture selector that exercises `POST /api/v1/databases/{name}/sql`.
Run the repository's required check only through its existing build owner after
the branch_restore compile blocker is handed off. Existing routed unit tests
are sufficient for evaluator semantics; add only the real caller-path and
generation/frontier regressions above.

## Forbidden shortcuts and return

Do not execute SQL independently per shard and merge rows, fall back to source,
turn a route gap into an empty relation, ignore session frontiers, swallow
generation conflicts, weaken existing assertions, or add retries/sleeps to hide
the race. Do not change public protobuf/OpenAPI/session-token contracts without
Astra's decision.

Execution is authorized for routine private cut validation and focused tests.
An outline checkpoint is required for any new public error/token/order contract
or any change to shared session-frontier representation. Return changed paths
and symbols, the generation/frontier evidence, test outcomes, build handle/log,
and unresolved decisions. Acceptance belongs to the coordinator; worker tests
provide evidence only.

## Evidence and recurring errors

Confirmed recurring pattern: routed code can pass direct `QueryStore` tests
while the real API still serves from a stale route because generation/session
admission is outside the evaluator. Prevention: every routed SQL brief must
include the server caller, pre/post generation checks, and a stale-token case.
Confirmed build pattern: focused Rust tests can be blocked by unrelated dirty
source changes before the selected test runs. Prevention: record the exact
compiler path/error and hand off the shared build; do not claim the routed test
passed or patch unrelated ownership.
