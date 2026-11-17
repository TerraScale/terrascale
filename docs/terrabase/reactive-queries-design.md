# Reactive queries over any SQL: design

Status: Proposed, not built. The user reviews this before any engine work starts.

Goal, in the user's words: "the goal of the terrascale sdks is to have auto refresh when any data changes similar to
how convex does it". Today the SDKs stay current only for a **shape**: the rows of one table matching a simple filter.
This design plans **live queries**: the current result of any read-only SQL query (joins, aggregates, subqueries,
parameters), pushed again whenever a commit could have changed it, at one commit offset per result.

## 1. Today: shapes

### 1.1 What a shape is

- One ordinary rowid table, an optional deterministic `where` over that table's own columns and an optional column
  list (`GetShapeEndpoint.cs:24-28`, [shape reads](shape-reads.md) §2 and §2a). `where` admits literals, operators,
  `CASE`, `CAST`, `COLLATE` and a fixed set of deterministic functions; subqueries, other tables, parameters and
  non-deterministic functions are refused with `400 unsafe_where` (`ShapeFilter.cs:44`, `ShapeFilter.cs:140`).
- Each `(table generation, where, columns)` has its own handle; the handle also binds the organization, project,
  database and incarnation (`ShapeHandle.cs:23`). DDL on the table, a restore or a copy changes the handle and the
  reader gets `409 must_refetch` (shape reads §8).

### 1.2 How a reader stays current

- **Snapshot, then diffs.** `offset=-1` redirects to the newest sealed snapshot; snapshot pages are read in row id
  order; the last page hands over a decimal commit offset; from there the reader asks for sealed chunks of whole
  commits `(O, E]` in commit order (`GetShapeEndpoint.cs:101-115`, shape reads §3 to §6).
- **One offset space per database.** An offset is the `change_id` of a commit record in the database's native change
  capture table. Every table of the database shares it; commits that do not touch the table still advance it
  (shape reads §3).
- **Diffs come from captured images.** Each captured row change carries its table, row id and before and after images
  (`SqlStatement.cs:12-23`). A shape compares both images with its `where` to emit `insert`, `update`, `delete` and
  move-in/move-out ops (shape reads §2a).
- **Live delivery.** `live=true` at the head waits up to 20 s for a commit, with a `TB-Shape-Cursor` bucket
  (`ShapeFlights.cs:35`, shape reads §7). A live request releases the organization's request lease before it waits
  (`GetShapeEndpoint.cs:90-99`). Every shape answer advertises the hints socket in `TB-Shape-Hints`
  (`GetShapeEndpoint.cs:146`).
- **Hints WebSocket.** `…/tables/{table}/shape/ws` speaks `terrascale.shape.v1` (`GetShapeHintEndpoint.cs:21-25`).
  It authenticates on the upgrade or with a first `auth` text frame (`GetShapeHintEndpoint.cs:112-118`), sends
  `ready` (`:160`), then loops on the same live wait as long-polling (`:168`) and sends `new_data` frames with the
  next offset and a CDN manifest (`:199`) or `provisional` frames for the region's own unconfirmed writes (`:187`).
  The client then fetches the data over HTTP; the socket carries hints, not rows. Keep-alive is 20 s (`:241-242`).
- **Limits.** 32 running flights and 4096 joined requests per machine, 8 and 1024 per organization
  (`ShapeFlights.cs:36-39`); 4096 live connections per machine, 1024 per database (`ShapeConnections.cs:8-9`); 128
  waiting replica requests per machine (`ReplicaShapeTails.cs:26`).

### 1.3 Home snapshots, replica tails

- The database's home seals snapshots and chunks from home-confirmed commits. A region holding a replica answers
  live waits from its own replica instead (`ShapeService.cs:343-348`, [region shape tails](
  region-shape-tails-and-cdc-retention.md)).
- A replica wait opens one held replica revision per check (`ReplicaShapeTails.cs:89`), polls every 50 ms or wakes on
  the overlay's change signal (`:122-123`) and answers `503 unavailable` when its replica has not reached the
  requested offset within 20 s (`:104-120`).
- A region's own writes appear first as `provisional`, then `confirmed` or `revert` ([shape overlay](
  shape-overlay.md)). The overlay holds at most 1024 transactions and 64 MiB per database (`ShapeOverlay.cs:34-35`)
  and is fed by every local commit's captured rows (`ShapeOverlay.cs:102`, `ILocalCommitFeed.cs:6`).

### 1.4 Access checks per read

- Every request resolves the shape again: scope `sql:read`, tenancy, the current generation, and for an end-user
  token the table's `read` rule and `columns`, read with the rows and bound as statement parameters
  (`ShapeRoute.cs`, `RulesReads.cs`).
- The hints socket re-authenticates and re-resolves the shape before every frame it sends and closes with
  `must_refetch` when the lineage changed (`GetShapeHintEndpoint.cs:177-181`, `:245-275`).
- [Rules](rules.md) replace grants with per-table `read` conditions and bind `auth.*` values into the handle
  (rules §4.3). End-user SQL stays refused (rules §6).

### 1.5 What is missing

A shape cannot join, aggregate, filter through another table or take parameters. An app that needs "my open todos
with their project name and comment count" must sync three tables and compute the answer on the client, and the
client must be allowed to read every row it joins through.

## 2. Recommended design: read-set invalidated re-execution

A live query is one read-only `SELECT` with parameters. The server runs it at a commit offset, remembers which data it
read (its **read set**), watches the commit tail, and runs it again when a commit's captured changes intersect the read
set. A client sees a sequence of complete results, each exactly the query's answer on the database at one commit
offset. This is the Convex model, with SQL instead of functions and the existing offset space instead of timestamps.

### 2.1 Accepted queries

- Exactly one statement, classified `select` by the SQL route parser, not `EXPLAIN`, with `TablesComplete` set and
  no `Unknown` flag (`SqlRouteParser.cs:14-20`, the same facts `WriteRouter.cs:173` routes reads with). Anything else
  is `400 unsupported_live_query`.
- Positional or named parameters, bound like SQL over HTTP. Parameters are part of the query identity.
- Deterministic only. `random`, `randomblob`, `date`/`time`/`datetime`/`julianday`/`unixepoch`/`strftime` with
  `'now'`, `current_date`, `current_time`, `current_timestamp`, `changes`, `last_insert_rowid`, `total_changes` and
  table-valued pragma functions are refused with `400 nondeterministic_query`. The app passes the time as a
  parameter.
- TerraBase's internal `_tb_*` tables are refused, as are the engine tables and functions SQL over HTTP denies
  (`SqlSandbox.cs:10-17`).
- Views are expanded: each view's SQL from `sqlite_schema` is parsed the same way, at most 8 levels deep, and its
  tables join the read set.

### 2.2 Read sets

The read set has two levels. Both are computed without engine changes.

1. **Tables.** Every base table the statement or its views read. Always present; sound by construction because the
   parser reports `TablesComplete`.
2. **Guards.** For each table in the outermost `FROM`, the top-level `WHERE` and inner-join `ON` conjuncts that name
   only that table's columns, literals and parameters, with the parameters bound. A row that fails a guard cannot reach
   the result: it is filtered before any join output, aggregate, window or `LIMIT`. Tables read inside subqueries,
   `LEFT JOIN` right sides, compound selects and views keep the table level only.

A captured change to a table invalidates the query when the table is in the read set and, for a guarded table, the
before image or the after image matches the guard. A missing image (an insert's before, the row-id-change insert the
pinned engine captures without an after image, shape reads §6) counts as matching. Guards are evaluated with the
evaluator that already compares captured images with a shape's `where` (shape reads §2a), so "my todos" queries are not
re-run by other users' todo writes.

A captured `sqlite_schema` change re-plans every live query of the database: unchanged plans keep running, a query
that no longer prepares gets an `error` frame with the SQL error and is dropped.

### 2.3 Invalidation from the commit tail

- One **tail reader per database per machine** reads the native capture table over `(O, head]` once for every live
  query of that database, in commit order, at most 65536 scanned changes per pass (the chunk sealer's bound, shape
  reads §5). A pass that hits the bound invalidates every query reading a table it touched, at table level.
- At home it wakes on the commit signal the live shape wait already uses. On a replica it shares the replica tail's
  single 50 ms poll and change signal ([live shape reads](free-reads-live.md): replica polling belongs to the shared
  computation).
- A **table index** maps each table to the live queries reading it, so a commit costs one lookup per touched table,
  plus one guard check per guarded query of that table.

### 2.4 Re-execute, not incremental

Invalidated queries run again from scratch. Results of interactive queries are small, the engine already plans and
runs them well, and re-execution keeps full SQL semantics with no second evaluator to keep correct. Two rules keep the
cost bounded:

- **Shared evaluation.** Query identity is `(database, incarnation, rules version, auth binding, normalized SQL,
  parameters)`, hashed like a shape handle. One evaluation serves every subscriber on the machine with that identity,
  the way flights share a shape read (`ShapeFlights.cs:36-39`).
- **Coalescing.** At most one evaluation per identity runs at a time; invalidations that arrive while it runs mark it
  dirty, and it runs once more at the newest head. Intermediate offsets are skipped: every result is a complete
  snapshot, so skipping one loses nothing.
- **Suppression.** Each result has a digest (SHA-256 of its canonical JSON). An evaluation whose digest equals the
  last delivered one sends nothing; the subscriber's offset still advances with the next transition (2.5).

### 2.5 Consistency

- Each evaluation runs in one read transaction and reads the head of that same transaction, as snapshots do
  (shape reads §4). Its result is exactly the query's answer at that offset `O`.
- A result computed at `O1` is still valid at `O2 > O1` when no change in `(O1, O2]` intersected its read set.
- **One offset per socket.** The server sends a `transition` frame only when every subscription of the socket has a
  result valid at the same offset `O`; the frame carries `O` and the subscriptions whose results changed. The client's
  whole view is the database at `O`, across all its live queries, as in Convex. Invalidated queries of one database
  are evaluated in one batch per head inside one read transaction, at most 5 s (`SqlLimits.cs:5`); a batch that does
  not fit continues at the next head and the socket waits for the next consistent offset.
- **Monotonic offsets.** A subscription never moves to a lower offset. On subscribe and reconnect the client sends its
  last offset as `min_offset`.

### 2.6 Regions

- A live query runs in the region that answers the request, on its replica when it holds one, and on the home
  otherwise; it is never forwarded to the home only to be fresher. It uses the same replica-read routing as shapes
  (`DatabaseRegions.ReplicaReads`, `GetShapeEndpoint.cs:50`).
- Offsets are home commit boundaries everywhere, so a replica's offset is comparable with the home's and never ahead
  of it.
- A region that has not reached the client's `min_offset` waits for its replica, at most 20 s, then answers
  `503 unavailable` with `retry_after`, as replica tails do (`ReplicaShapeTails.cs:104-120`). Moving to another region
  therefore never shows an older database.
- Results contain home-confirmed data only. A region's own provisional writes are not run through SQL: the SDK shows
  them with the optimistic layer `useMutation` already has, until a result's offset reaches the write's confirmed
  offset (step 9 adds that offset to write answers).

### 2.7 Access

- Live queries take `sql:read`. API keys, project tokens and platform sign-in tokens may run any accepted query, like
  SQL over HTTP.
- End users never send SQL (rules §6). They run **named queries** that the database owner deploys with the rules set:
  `"queries":{"myTodos":{"sql":"SELECT … WHERE t.owner_id = :auth_uid AND t.done = :done","params":["done"]}}`.
  `auth.*` values are bound as parameters, never spliced, and enter the query identity, like the rules binding enters
  the shape handle (rules §4.3). A rules deploy ends every named live query with `must_refetch`.
- Before every `transition` frame the socket authenticates again and resolves scope, tenancy, rules version and
  database as the hints socket does (`GetShapeHintEndpoint.cs:177`, `:245-275`). The socket closes at the token's
  `exp`.

### 2.8 Limits and cost

| Limit | Value | Over it |
| --- | --- | --- |
| Result | 10,000 rows and 1 MiB of JSON | `error` frame `live_result_too_large`; use a shape or a `LIMIT` |
| Evaluation time | 5 s (`SqlLimits.cs:5`) | `error` frame `query_timeout`, subscription dropped |
| Subscriptions per socket | 256 | `error` frame `too_many_subscriptions` |
| Distinct live queries per database per machine | 1024 | `429 live_busy` |
| Running evaluations | 32 per machine, 8 per organization | queued, coalesced |
| Live sockets | Shared with shapes: 4096 per machine, 1024 per database (`ShapeConnections.cs:8-9`) | `429 shape_busy` |

Memory for every result, read set and queued frame is reserved through the memory budget before it is built, and
released on errors and cancellation. A commit costs one tail pass per database plus one table-index lookup per touched
table; a write costs re-executions only for queries whose read set it hits.

Billing follows the [operation allowance](operation-allowance.md): SQL reads count toward fair use per returned row.
The first result and every changed result count as one SQL read of the delivered rows; suppressed evaluations count
nothing. Metrics: evaluations by outcome (`changed`, `unchanged`, `failed`), invalidations by level (`table`, `guard`),
tail pass duration and live query count, all with bounded labels.

### 2.9 Socket and HTTP protocol

One socket per client and database: `GET /v1/projects/{project}/databases/{database}/live/ws`, subprotocol
`terrascale.live.v1`, OpenAPI tag `Live`. It reuses the hints socket's mechanics: authorization on the upgrade or a
first `auth` frame within 5 s, `ready`, 20 s keep-alive, per-frame re-check, shared connection slots. Shape
subscriptions move onto the same socket, so a page holds one socket however many shapes and queries it shows.

Client frames:

```json
{"type":"auth","token":"…"}
{"type":"subscribe","id":"q1","sql":"SELECT p.name, count(*) AS open FROM todos t JOIN projects p ON p.id = t.project_id WHERE t.done = ? GROUP BY p.name","params":[0],"min_offset":"812","digest":"…"}
{"type":"subscribe","id":"q2","query":"myTodos","params":{"done":0}}
{"type":"subscribe","id":"s1","shape":{"table":"todos","where":"done = 0"},"handle":"h1-…","offset":"812"}
{"type":"unsubscribe","id":"q1"}
```

Server frames:

```json
{"type":"ready"}
{"type":"transition","offset":"815","results":[
  {"id":"q1","digest":"…","columns":["name","open"],"rows":[["Inbox",3],["Work",7]]}],
 "shapes":[{"id":"s1","handle":"h1-…","next_offset":"815","manifest":{…}}]}
{"type":"error","id":"q1","code":"query_timeout","message":"…"}
{"type":"error","code":"must_refetch"}
```

- `rows` are arrays in `columns` order with the value encoding of SQL over HTTP (blobs as `{"base64":"…"}`).
- A subscribe with the `digest` of the client's last result at `min_offset` gets no rows when the result is unchanged:
  the first `transition` lists the id with its digest only.
- Shape entries keep shape semantics: a hint with the next offset and manifest; the client fetches chunks over HTTP.
- HTTP long-poll stays for clients without sockets: `POST …/databases/{database}/live` with one query, `min_offset`,
  `digest` and `cursor` answers at once when the result changed, else waits up to 20 s like `live=true` and answers
  `up_to_date` with the newer offset.

### 2.10 SDK surface

`db.live(sql, params)` returns a live query that is a `ReactiveQuerySource` (`sdk/js/src/react/core.js`), so C5's hooks
take it unchanged:

```js
const open = useQuery(db.live("SELECT p.name, count(*) AS open FROM todos t JOIN projects p ON p.id = t.project_id WHERE t.done = ? GROUP BY p.name", [0]));
const mine = useQuery(db.live({ query: "myTodos" }, { done: 0 }));
db.live(sql, params).subscribe(({ rows, columns, offset }) => render(rows));
```

- The value has the shape subscription's form, `{ rows, columns, offset }`, so code switching from
  `db.shape("todos")` to `db.live(…)` keeps its callback. Rows are objects keyed by column name.
- `must_refetch` and a rules deploy map to the `generation-changed` reset; `live_result_too_large` and `query_timeout`
  map to a `subscription` error.
- .NET `db.Live(sql, params)` returns `IAsyncEnumerable<LiveResult<T>>`, Go `db.Live(ctx, sql, args...)` returns
  `iter.Seq2[LiveResult, error]`, Python `async for result in db.live(sql, params)`, Rust `db.live(sql, params)`
  returns a `Stream<Item = Result<LiveResult>>`, each next to the shape subscription C6-C9 added.

## 3. Alternatives considered

**Re-run on every commit, no read set.** The simplest server: every live query of a database runs again after every
commit. Cost grows with queries times commits, and a busy table re-runs every query of every user. The table-level
read set (step 3) costs a little more code and removes all work for unrelated tables.

**Table-level read sets only.** Correct and engine-free, and it is step 3. As the end state it is too coarse: one write
to `todos` re-runs every user's "my todos" query, O(users) work per write. Guards (step 6) cut that to the queries whose
filters match the changed row.

**Incremental view maintenance.** Compile each query into a dataflow (DBSP, Noria, Materialize style) and apply
captured changes as deltas; the engine also has experimental incremental views. It supports a subset of SQL, keeps
per-query operator state in memory, and makes correctness depend on a second evaluator, or on an experimental engine
feature we may not fork. Re-execution of small interactive results is cheaper to build and to trust.

**Client-side SQL over synced shapes.** Sync the needed tables as shapes and run the query in a local SQL engine in
the client (Electric with an embedded database, Zero style). It ships every row of every joined table to the client,
which rules would then have to allow, and needs an embedded SQL engine in five SDKs. Useful later for offline apps,
not for "auto refresh like Convex".

**Engine-recorded read sets.** Record the exact key ranges each b-tree cursor visited during execution, as Convex
records index ranges. It is the most precise invalidation, also through joins and subqueries, but needs
instrumentation in the storage engine's virtual machine that upstream does not offer, and we never fork the engine.
Deferred to step 12, only if metrics show guard misses dominate.

**Triggers.** Create per-query triggers that write to a notification table. Reads would create schema and writes,
triggers do not see views or aggregates any better than the parser, and they would replicate as customer schema.

## 4. Delivery

Each step is one PR of at most about 400 lines, with its journey step where a user can see the change.

| # | Step | Engine risk | Replication risk |
| --- | --- | --- | --- |
| 1 | `LivePlan`: accept one read-only `SELECT`, refuse non-deterministic functions and internal tables, expand views, produce the table read set (`Features/Live/`). Parser facts only. | None | None |
| 2 | Evaluator: run a plan in one read transaction at home and on a held replica revision, returning offset, columns, rows and digest within the result, time and memory limits. | Low: reads the head inside the same transaction, as snapshots do | Medium: replica offset must be the applied home boundary |
| 3 | Tail reader and table index per database per machine; coalescing scheduler with shared evaluation and machine and organization budgets; metrics. | Medium: a second reader of the capture table beside the sealer | Medium: replica capture visibility and the shared 50 ms poll |
| 4 | `…/live/ws` (`terrascale.live.v1`): auth, subscribe and unsubscribe, consistent `transition` frames, `min_offset` and `digest` resume, per-frame re-check, limits, OpenAPI. | Low | Medium: `min_offset` waits on replicas |
| 5 | JS `db.live` and its `ReactiveQuerySource`; React and Svelte `useQuery` take it. Journey: a page shows a joined count that changes after a write from a second session. | None | None |
| 6 | Guards: the route parser reports per-table top-level conjuncts; guards are checked against captured images with the shape filter evaluator. Journey: a write by another user does not re-send the first user's result. | Medium: native parser change, shipped in the pinned native release | None |
| 7 | .NET and Go `Live`, with their `e2e/sdk/operations.json` operation. | None | None |
| 8 | Python and Rust `live`, with their operation. | None | None |
| 9 | SQL write answers carry the confirmed commit offset; SDKs pass it as `min_offset` and drop the optimistic layer at that offset. | Low | High: forwarded writes, in-region acknowledgement and home apply must all report the home boundary |
| 10 | Shape subscriptions on the shared socket; the five SDKs move; `…/shape/ws` and `terrascale.shape.v1` are deleted. | None | Low |
| 11 | Named queries in the rules set (after the rules rollout): deploy checks, `auth.*` binding, simulator support. | None | Low: rules replicate with the database |
| 12 | Only on evidence: engine-recorded key-range read sets, contributed upstream. | High | Medium |

Steps 1 to 5 give project credentials Convex-style live SQL in JavaScript. Steps 6 to 9 make it cheap, complete across
SDKs and read-your-writes. Steps 10 and 11 bring it to end users.

## 5. Open questions for the user

1. **End users first or project credentials first?** End users need named queries (step 11), which build on the rules
   rollout. Recommendation: project credentials first (steps 1-9); named queries right after rules ship.
2. **Do named queries obey table `read` rules?** Applying row conditions inside arbitrary joins and aggregates is hard
   to make sound (a `count(*)` leaks hidden rows). Recommendation: no; a named query is owner-authored code, like a
   Convex query function, bound to `auth.*` parameters, and the rules simulator shows what it returns for a user.
3. **Billing.** Recommendation: the first result and every changed result count as SQL reads of the delivered rows;
   unchanged re-executions count nothing; evaluation work is bounded by the limits in 2.8, not billed.
4. **Result size.** Recommendation: whole results up to 10,000 rows and 1 MiB, no row-level diffs; larger data uses
   shapes. Add digest-keyed row diffs only if metrics show large results changing often.
5. **Provisional writes in results.** Recommendation: no; results stay home-confirmed and the SDK's optimistic layer
   covers the gap (2.6). Running SQL over the provisional overlay would need writes on replicas.
6. **One socket for shapes and queries.** Step 10 removes `…/shape/ws`, a breaking change. Recommendation: yes, before
   go-live, while the SDKs are unpublished.
7. **Non-deterministic functions.** Recommendation: refuse them; apps pass the time as a parameter. The alternative,
   re-running time-based queries on a timer, adds work with no commit to trigger it.
8. **Engine-recorded read sets.** Recommendation: do not start step 12 unless more than half of re-executions return
   an unchanged result for a month of production traffic.
