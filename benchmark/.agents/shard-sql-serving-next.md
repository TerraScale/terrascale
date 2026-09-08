# Cross-shard SQL serving checkpoint

Status: design checkpoint only. No serving or API code is changed by this
packet.

## Observed integration path

`crates/terrabase-server/src/api.rs::execute_sql` is the real public caller.
It resolves one `DocumentHost`, compiles the read-only SQL through
`Engine::queries()`, obtains one `ReadSnapshot` (or one session snapshot), and
calls `QueryStore::execute`. That path currently executes against the one
`Engine` behind the host. After a shard split, the source Engine can retain
older rows while the target Engine owns the moved key range, so a successful
document GET and the SQL route can observe different files.

`RouteParticipantRegistry::load` in `src/member/shard_registry.rs` already
loads the current durable `ShardRouter`, validates every route participant,
and keeps the source plus target `Arc<Engine>` values. Its existing
`participant` method is intentionally single-key and rejects a mixed-key
request. SQL needs a read coordinator over all current participants, not a
single participant selection and not a source fallback.

The router exposes sorted inclusive-lower/exclusive-upper `ShardRoute` ranges
and a generation. The canonical key is
`canonical_shard_key(collection, id)`. The route generation and participant
set therefore form the immutable serving assignment for one SQL execution.

## Accepted execution contract

Add a private `RouteParticipantRegistry::sql_snapshot`/executor seam consumed
by `execute_sql` after plan compilation. The seam must:

1. Revalidate the registry generation before opening reads. A changed route
   returns a conflict/retry result; it never silently serves the old source
   file.
2. Capture one bounded read snapshot per admitted participant. The snapshot
   is a per-lane `(route generation, shard id, storage sequence)` cut, bound to
   the authenticated tenant/database scope. It is a causal consistent cut;
   SQL does not advertise or imply linearizability.
3. Require each participant to cover the caller's session lane frontier before
   reading. A missing or lagging lane returns the existing session coverage
   refusal, with the safe Region/frontier remedy. An absent session uses the
   current local cuts and does not manufacture a global session token.
4. Execute the same compiled SQL against bounded participant views and merge
   rows centrally using canonical shard keys. The result ordering, projection,
   parameters, output-row/byte limits, deadline, cancellation, and SQLite VM
   progress limits remain the existing `QueryStore::execute` invariants.
5. Exclude source rows whose canonical key belongs to a target route. The
   source participant may be read for its own route intervals only; it is not
   queried as a catch-all. A target participant is likewise restricted to its
   assigned intervals. This prevents stale moved copies from entering the
   merged result.
6. Refuse a route gap, missing participant, generation mismatch, or incomplete
   lane frontier. Do not convert any of these into an empty result or a
   source-only query. The caller maps the bounded refusal to the existing
   retry/conflict or session-coverage response.

The coordinator should use existing collection snapshot interfaces where
possible (`snapshot_documents_in_collection` and its filtered variants), with
the SQL executor remaining the final evaluator. If the current SQL runtime
cannot accept multiple bounded source views, introduce one private
`QueryStore` execution seam that takes the route-participant snapshots; do not
reparse SQL or create a second parameter/coercion policy. The route registry
owns participant selection and range filtering; the query layer owns SQL
semantics and result limits.

## State and invariants

The read state is immutable for the request:

```text
route generation + authenticated scope
  -> validated participant/range set
  -> one causal snapshot per participant
  -> per-lane filtered SQL source
  -> bounded central merge
```

The following must hold for every emitted row:

- its canonical key belongs to exactly one route in the captured table;
- it came from that route's participant and snapshot sequence;
- it belongs to the authenticated tenant and database incarnation;
- no participant was read below its required session lane frontier;
- no source copy outside its current range was admitted to the merge.

The merge may observe different physical sequences across lanes. The public
behavior is therefore causal snapshot consistency, with a route generation
and lane frontier bound in the session/result metadata where the existing
contract exposes one. It must not claim an atomic SQLite snapshot across
files.

## Integration sequence

1. Extend `RouteParticipantRegistry` with a read-only route snapshot that
   returns validated `(ShardRoute, Arc<Engine>, ReadSnapshot)` participants,
   bounded by a fixed participant count and the existing read permits.
2. Add the query execution adapter that evaluates each participant only over
   its canonical key interval and merges the bounded rows under the existing
   SQL result limits.
3. Change `execute_sql` to obtain the route snapshot from the admitted host
   and call that adapter. Preserve the current single-engine path for a
   registry with one route through the same seam.
4. Add the session-frontier check at snapshot admission. A session token must
   cover every lane selected by the route generation; a token from a retired
   route is stale and refuses.
5. Recheck route generation before returning the result. A cutover during the
   read returns conflict/retry rather than a mixed-generation response.

## Required integration case

Use the real server fixture and caller path:

1. create a database and seed a document in the source range;
2. perform the accepted split and wait for target admission;
3. patch or put a document whose canonical key is in the moved range through
   the normal document API;
4. issue `POST /api/v1/databases/{name}/sql` for that collection through the
   same host/session fixture;
5. assert SQL returns the target's current value exactly once, while the
   source stale copy is excluded;
6. repeat with a two-collection aggregate spanning both route intervals and
   assert the bounded causal merge, then exercise a stale generation/session
   token and assert the explicit refusal.

The focused check must run through the server fixture and the current
`RouteParticipantRegistry`; a direct `QueryStore` unit test alone does not
qualify the runtime integration.

## Limits and unresolved implementation choices

This checkpoint does not choose a new public response shape, a new session
token version, or a new cross-file ordering promise. Those decisions remain
within the existing causal session contract. If the current SQL executor can
only materialize one SQLite connection, the root must release the private
multi-source adapter boundary before implementation; serving code must not
mask the split by rejecting all SQL or falling back to the source file.
