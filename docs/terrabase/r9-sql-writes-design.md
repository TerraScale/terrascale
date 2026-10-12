# R9: SQL writes as lattice operations (design)

- Status: design only, by the R3/R6/R7 owner, 2026-10-12. No code yet. R9 starts after the phase-1 integration
  candidate is frozen and accepted (COS). It refines section 12 of [multi-region-replication.md](multi-region-replication.md).
- Acceptance: E11 (`LatticeE2ETests.E11_sql_update_in_gru_and_patch_in_sjc_both_survive_and_increments_add`) as an
  actual pass, its row removed from `Gaps`, then 3 serialized repeats each by the R9 owner and the strict-test owner.

## 1. The rule

**A SQL write is a document-API write.** `POST …/sql` compiles an `INSERT`, `UPDATE` or `DELETE` into the same
`DocumentOperation`s that the transaction endpoint commits (`IDocumentService.WriteAsync`, one operation per affected
row), in one storage write transaction. Nothing below the document service learns about SQL: the operations write
`DocumentState` entries at the transaction's stamp, the CDC tailer ships them, peers join them, and GC, refill,
stale changes and anti-entropy treat them exactly as API writes. So SQL writes and API writes converge with each other
by construction, and every existing lattice test also covers SQL.

Rejected: executing the DML in SQLite and capturing its CDC rows (section 12's "execute-and-capture"). It needs
statement cancellation Turso does not have, and it would make plain column writes the source of truth instead of the
lattice.

## 2. Grammar and translation

`SqlSubset` keeps its parser (SqlParserCS, SQLite dialect), bounds (16 KiB, 256 conditions, 1024 values, no joins,
subqueries or functions in `WHERE`) and error style (messages never quote the client's text). It returns a read or
one of three writes.

| Statement | Rows | Operation per row |
| --- | --- | --- |
| `INSERT INTO c (_id, f, …) VALUES (…), …` | each `VALUES` row | a create-only `put`, as the document API's `If-None-Match: *` (an existing live document: 412 `precondition_failed`, local check). `_id` omitted: a new ULID |
| `UPDATE c SET … WHERE <filter>` | the compiled `SELECT _id FROM c WHERE <filter>`, run in the write transaction | a `patch` with ops (below) |
| `DELETE FROM c WHERE <filter>` | the same select | a `delete` (a tombstone) |

`SET` items become ops of section 13, so they get the same per-field semantics as `PATCH …/vnd.terrabase.ops+json`:

| `SET` item | Op | Merge across regions |
| --- | --- | --- |
| `f = <literal>` (string, number, boolean, JSON via `json('…')` is out of v1) | `set` | per-field last-writer-wins on the HLC stamp |
| `f = NULL` | `unset` | per-field LWW (a tombstone base) |
| `f = f + <int>` / `f = f - <int>` (the same field, an int64 literal, non-zero) | `inc` | PN-counter: concurrent increments add |
| `f = <other field or arithmetic>` | refused in v1 (400): a value computed from local state is not convergent, and silently writing it as LWW would surprise | — |
| `a.b = …` (nested) | refused in v1 (400 `nested_lattice_path`, as the ops API) | — |

- One statement is one write transaction and one stamp. Every row's operations share it ("several writes of one
  document in one transaction share their version"). A field written twice in one `SET` is a 400.
- `WHERE` runs against local state at write time, on the writer connection, inside the transaction: rows a concurrent
  remote write would have matched are not seen. Eventually consistent, like an API write chosen by a local read.
- Unique-constraint losers are invisible to SQL (unique-constraints.md), so `UPDATE`/`DELETE … WHERE` never touch a
  hidden loser. It stays reachable by id through the document API.
- Sets (`add`/`remove`) have no SQL syntax in v1. `SET tags = '[…]'` on a set field writes a base: LWW replaces the
  set, as the ops API's `set` does.
- Rejected with 400: `INSERT … SELECT`, `ON CONFLICT`, `REPLACE`, `RETURNING`, `UPSERT`, DDL, `PRAGMA`, `ATTACH`,
  several statements, writes to reserved collections (`_collections`, `_indexes`, `_unique`) and `_tb_*`.
- Scope: reads keep their scope. A write needs `documents:write` as well, checked after parsing (the statement kind is
  only known then). Same route `POST …/sql`.
- Response: `{"affected": n, "ids": [… up to 1000]}`.

## 3. Limits and transactions

- **Atomic up to one change group.** A statement writes at most 1024 documents and 2 MiB of captured state and
  bodies (`Capture`), the same bounds as any transaction, so it is one change group: it commits whole and replicates
  whole. The compiled select reads `LIMIT 1025`; 1025 rows is 400 `sql_too_many_rows`; a capacity overflow is answered as a
  transaction's (`ChangeGroupCapacityException`: 503 `capacity`; storage write limits: 413). Nothing commits in either case.
- Chunked statements (up to 10,000 rows, non-atomic, section 12) are deferred to slice R9c and may be dropped: a
  non-atomic statement is a new contract, and the transaction API already covers bulk writes.
- Unique checks run per operation in order, as in a transaction: a duplicate is 409 `unique_violation` with
  `error.row` (the zero-based row), and the whole statement rolls back.
- No `BEGIN`/`COMMIT`: one statement per request. Multi-statement atomicity is the transaction API's.
- A retried `SET f = <literal>` or `DELETE` is idempotent. A retried `SET n = n + 1` adds again, as a retried `inc`
  does: documented, not hidden.

## 4. What E11 asserts

`Cluster.StartAsync()` (gru and sjc, in-process nodes, peer streams):

1. Seed `{"a":"base","b":"base","n":0}` through gru; wait until both regions serve it.
2. At once: on gru `UPDATE "<collection>" SET a = 'gru' WHERE _id = '<id>'` (POST `…/databases/<db>/sql`), on sjc
   `PATCH` (merge patch) `{"b":"sjc"}`.
3. Settled (both regions identical and present): `a == "gru"` and `b == "sjc"`. Per-field LWW: neither edit is lost.
4. At once on gru and sjc: `UPDATE "<collection>" SET n = n + 1 WHERE _id = '<id>'`.
5. Settled: `n` is exactly `2` (`ToJsonString() == "2"`). The increments add instead of last-writer-wins to 1.

Today it fails at step 2 with 400 "only SELECT statements are supported" (its gap signature). Each 60 s convergence
gate is the harness's; R9 changes no limit.

## 5. Failure modes to test

| Mode | Test (suite) | Expected |
| --- | --- | --- |
| Concurrent SQL vs API, other fields | E11 step 2 (E2E) | both survive |
| Concurrent `n = n + 1` | E11 step 4 (E2E); Fast: 50 increments split over 3 machines | sum |
| `SET n = 0` after increments, concurrent with an increment | Fast | E8 semantics: the reset's epoch wins over older increments, a concurrent increment survives |
| Partition | E2E, harness `Partition`/`Heal`: SQL updates on both sides of a cut, then heal | per-field LWW, counters add, identical everywhere |
| Restart mid-statement | Fast: fail the write transaction after half the rows (storage fault hook), restart | nothing of the statement committed or shipped (atomic) |
| Process kill after commit, before response | E2E region processes (`SIGKILL` after the commit hook) | the statement ships after restart; the client's retry of a literal `SET` is a no-op |
| Stale change | Fast (`ShiftedTime`, `StaleEditAsync` with a SQL `UPDATE`): queued > 24 h while gru deletes and collects | loses to the collected deletion everywhere (R7 rule, same as an API write) |
| GC interplay | Fast: SQL `DELETE`, collection below the horizon, then `INSERT` of the same id | no ghost fields (E14 rule); the new document only has its own fields |
| Unique | Fast: concurrent `INSERT`s of one unique value in two regions | smallest id wins everywhere; `UPDATE … WHERE` does not see the loser |
| Limits | Fast: 1025 rows; 2 MiB of bodies; 257 conditions | 400 / 503 `capacity` / 400, nothing committed |
| Scope | Fast: a `documents:read` key posts an `UPDATE` | 403, nothing written |
| Grammar | Fast: every rejected form of section 2 | 400 with a message that quotes no client text |

## 6. Slice plan (each ≤ 1,500 lines, no storage format change)

| Slice | Content | Gate |
| --- | --- | --- |
| R9a | Grammar for `UPDATE` (`set`, `unset`, `inc`) and `DELETE`; compiled select in the write transaction; operations through `WriteAsync`; scope; one-group limit; response | E11 actual pass (3+3 repeats); Fast for limits, scope, grammar, restart mid-statement |
| R9b | `INSERT` (multi-row `VALUES`, generated ULIDs, create-only puts), unique `error.row` | Fast: unique, GC re-create, stale change; E2E partition |
| R9c (optional) | Chunked statements past one group, only if COS/user keep section 12's 10,000-row contract | its own tests; otherwise section 12 is amended to "one group" |

The storage format stays `StorageFormat.Id` of the base (no schema or encoding change); each slice checks it.

## 7. Open questions (decisions for COS/user, not taken here)

1. Keep the 10,000-row chunked statements of section 12 (non-atomic), or limit SQL writes to one atomic group
   (recommended)?
2. Refuse computed `SET f = <expression of other fields>` in v1 (recommended), or allow it as a non-convergent LWW
   write?
3. Writes on the same `POST …/sql` route with an added `documents:write` check (recommended), or a separate route?
