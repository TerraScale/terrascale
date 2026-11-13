# Write routing (PIVOT R4 + R5)

- Status: implemented 2026-11-04. Router decides and labels every write; only the forward path commits until in-region
  quorum acks (R10) and the region-to-home push (R7) exist.
- Code: `src/TerraBase/Features/Sql/Routing/` (router, schema facts, native parser binding),
  `Features/Sql/ReplicaSqlSession.cs` (psql and WebSocket sessions), `Features/Sql/SqlExecuteEndpoint.cs` (REST),
  `Features/Replicas/HomeSessions.cs` (`batch` call), `Features/Regions/HomeForwarding.cs`.
- Differential proof (R13): 10,000 random transactions in local mode (sjc, `TerraScale-Durability: home`) and
  home-only mode gave the same final rows, or a `terrascale.conflicts` record for every difference; results in
  `benchmark/bench/2026-11-05-r13-router-differential.md`.

## Where a write goes

A region that holds a replica of a database (see [region replicas](../region-replicas.md)) parses each write with the
engine's own parser (`libterrascale_sql_parser.so`, W0-10) and reads the table's schema facts from its replica
(primary key, rowid alias, `AUTOINCREMENT`, unique indexes, foreign keys in both directions, triggers). It then picks
a path from PIVOT Appendix E:

| Write | Decision | `reason` |
|---|---|---|
| INSERT naming the primary key | home | `client_key` |
| UPDATE by the whole primary key with constant values, not touching a key, UNIQUE or FK column | local | `update_by_key` |
| DELETE by the whole primary key of a table nothing references | local | `delete_by_key` |
| Relative UPDATE (`SET n = n - 1`) | home | `relative_update` |
| INSERT into a table with a secondary UNIQUE; UPSERT, `OR REPLACE`, other conflict clauses | home | `unique` |
| UPDATE of a primary key or UNIQUE column | home | `unique_change` |
| INSERT into an `AUTOINCREMENT` table | home | `autoincrement` |
| INSERT into a rowid table, or without naming the key | home | `rowid_key` |
| DELETE from a table another table references | home | `fk_parent_delete` |
| UPDATE of a foreign key or referenced column | home | `fk_change` |
| UPDATE or DELETE not by the whole key | home | `not_by_key` |
| DDL | home | `ddl` |
| `WITH`, `INSERT … SELECT`, `UPDATE … FROM` | home | `complex` |
| A table with triggers | home | `trigger` |
| A transaction that reads and then writes | home | `read_then_write` |
| Anything the parser or schema facts cannot place | home | `unknown` |

A transaction or REST batch goes local only when every write in it does; otherwise the whole transaction goes home.
When in doubt the router sends it home.

**Commit path.** A write decided local commits in the region only when the region has at least two nodes in the peers
file (D16), a local commit path for the database is ready (`ILocalCommitPath`, provided by R7/R10), and the node's
clock issues the commit stamp ([HLC](../hlc-clock.md#clock-trust)); a node whose clock is not trusted refuses the stamp
and the write goes home. Until then every write commits at the
home: `commit=home`. Nothing is configured; the router reads these facts on each decision. A timed-out local publish is
forwarded with `TerraBase-Publish-Id` set to the same transaction id, and the home claims that id so the write applies
once. A push stream that returns NATS error 10074 is not retried, because the stream wants more replicas than the cluster has.

## Forwarding

- **REST** (`sql/execute`): a batch with a write, a request before the replica's first pull, and a request with
  `TerraScale-Write: home` are forwarded whole to the home over the private mesh in one hop (the forwarding of
  [cross-region](../cross-region.md)); the home authenticates, meters and runs it as one transaction. The answer is the
  home's, with the statements' rows (`RETURNING` included) and two headers:
  `TerraScale-Write-Route: route=<local|home>; reason=<reason>; commit=<local|home>` and
  `Server-Timing: home;dur=<ms>;desc="<home region>"`. A home that cannot be
  reached answers `421 wrong_region` naming it; see [unanswered forwards](#unanswered-forwards).
- **421 retry URL**: every `421 wrong_region` carries `https://<home>.api.terrascale.tech<path>?<query>` (the same
  path and query) in `error.url` and in `Location`; clients retry there.
- **WebSocket**: an `execute` frame with a write (or any frame inside a transaction open at the home) runs at the home
  as one `batch` call on the session's home session: one round trip for the whole frame.
- **Postgres wire**: the connection is pinned to one home session for every write and transaction. A transaction
  starts in the region on a replica snapshot, so read-only transactions never leave it. Its first write, when nothing
  was read yet, opens the transaction at the home and runs it there.

## Unanswered forwards

A forwarded request that gets no answer from the home (or, inside a region, from the regional writer) answers by what
the home could have done with it:

- `421 wrong_region` when nothing was written there: the request is a `GET` or `HEAD`, which never writes, or the connection
  failed before the request was sent (name resolution, connect or TLS refused). Resend it to the named region.
- `503 write_outcome_unknown` with `error.region` naming the home for any other request: it was sent, or may have been
  (a connect timeout cannot be told from a lost answer), and the connection broke or timed out before the answer. The
  write may have committed. No SDK resends it; read what it would have written before running it again.

A WebSocket relay that cannot open its socket to the home answers `421`: the upgrade carries no SQL.

## `40001` retry

A psql or WebSocket transaction that read in the region and then writes cannot keep those reads: they came from the
replica, not from the home where the write commits. The statement fails with SQLSTATE `40001`
(`serialization_conflict`) and the transaction is aborted. Drivers and ORMs retry it. The router remembers the
transaction's first statement (its fingerprint: the statement with literals removed, per database, in memory, at most
65,536); a later transaction that starts with it opens at the home straight away, so the retry succeeds.

## Overrides

| Surface | How | Values |
|---|---|---|
| Postgres wire | `SET terrascale.write = 'home'` (session) | `home`, `local`, `auto` (default) |
| REST, WebSocket | `TerraScale-Write` request header (on the upgrade for WebSocket) | same; anything else is `400 invalid_request` on REST |

`home` sends every transaction and read of the session or request to the home (read your writes without waiting for a
pull). `local` is accepted and ignored.
`SHOW terrascale.write_route` answers the session's last decision.

## Metric

`terrabase.sql.write_routes{route, reason, commit, region}`: decisions of replica regions, one per write statement,
REST batch or WebSocket frame.

## Known gaps

- Per-table overrides in the dashboard are not built yet.
- psql simple-query strings with several statements run one statement per home round trip.
