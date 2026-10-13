# SQL and document value regressions — 2026-10-13

Source revision: `e30ffdc287530551b161b0130220be53d3c0475e` (both local `origin/main` and the remote main ref when inspected).
Test artifact: `tests/TerraBase.Slow/Management/QueryValueRegressionTests.cs`, in the existing `TerraBase.Slow` integration executable.

## Scope and contracts

Seven independent tests start real local Kestrel listeners with the existing `ManagementApi` / `TerraBaseTestServer` harness. Each owns its server, temporary storage and clients; TUnit retains its default parallel execution. Writes use the real document PUT route. No production source, existing tests, shared fixture, project, build or workflow files change.

The inspected contracts are:

- `src/TerraBase/Features/Management/Queries/SqlSubset.cs` and `QueriesExtensions.cs`: read-only SELECT subset; signed 64-bit integer literals, finite double literals, string and boolean literals; placeholders are rejected. `SqlRequest` in `IQueryService.cs` has only `Sql`, with no parameter-binding input.
- `src/TerraBase/Features/Management/Queries/QueryService.cs`: whole-document and dotted-field projections, null for missing projected members, result limits and truncation.
- `src/TerraBase/Engine/Sqlite/SqliteStorageTransaction.Queries.cs`: actual parameter binding, `json_extract` comparisons, case-sensitive prefix ranges and deterministic id ordering.
- `src/TerraBase/Primitives/DocumentJson.cs`, `ExactNumber.cs`, and the serialization methods in `Engine/Lattices/DocumentState.cs`: JSON objects, preserved JSON value types and numeric tokens, at most 128 significant digits, and magnitudes from `1e-307` to below `1e308` (or zero).

Coverage groups:

1. Stored JSON and SQL projections: null, empty string/array/object, booleans, signed 64-bit bounds, 128-digit numbers, fractional precision, exponent bounds, negative zero, Unicode, embedded NUL and escaped characters.
2. SQL predicates: null versus missing, empty and ordinary strings, boolean versus string literals, adjacent integers around `2^53` and both signed 64-bit endpoints, exact composed/decomposed Unicode, quotes and Unicode prefix ranges.
3. Result/error semantics: empty collection and empty filter results, exact and exceeded LIMIT, honest named/positional placeholder rejection, integer/decimal overflow, unsupported UNION/subquery shapes and malformed SQL.
4. Cancellation: cancel after the first actual engine row, assert the next read throws, then query again through the same transaction and the HTTP SQL route. This covers cancellation at a deterministic consumer boundary; it does not claim interruption inside a native call.

## Collision inspection and gaps

There is no checkout `.agents` directory or additional nested test instruction file. The referenced sibling `terrabase-rust` contract checkout is unavailable; assertions use the implemented C# contracts above.

Open PR file lists were inspected for #125, #152, #165–170, #116–117, #120–121. Relevant diffs of #116, #121, #168 and #169 were read: they own storage/catalog or replication/harness changes, including `SqliteStorageTransaction.Queries.cs`, `DocumentFiles.cs` and `FakeChangeGroupSource.cs`. None owns the new test file. Those files remain untouched. These tests are based on main, not the held replication candidates.

No vector, FTS or view-query route is implemented in the inspected TerraBase query surface. No coverage is claimed for those features, HTTP request validation, cursors, replication, backup/restore, auth, imports or health.

SQL uses SQLite scalar comparisons; document queries also apply type checks. This artifact does not define a new cross-type coercion or arbitrary-precision SQL comparison contract. Its exact-number assertions cover storage/projection preservation and signed integer equality.

## Focused reproduction

SDK: `11.0.100-rc.1.26425.128`, as pinned in `global.json`.
The existing Slow project references ts-auth, which references the sibling SDK. This machine's SDK checkout is `/home/mariogk/Projects/TerraScale/ts-dotnet`, revision `df852e3b4d3a4df324452d46792b20566eea5136` when inspected.

The Makefile's Slow target builds the entire TerraBase solution filter. To respect the requested focused local validation, use the project directly with the existing shared build lock:

```sh
flock -w 1800 /tmp/ts-heavy.lock timeout 240 nice -n 19 \
  env TsDotnetRoot=/home/mariogk/Projects/TerraScale/ts-dotnet \
  dotnet build tests/TerraBase.Slow/TerraBase.Slow.csproj \
  --configuration Debug -p:RestoreLockedMode=true --nologo -v minimal

flock -w 60 /tmp/ts-heavy.lock timeout 180 nice -n 19 \
  dotnet run --project tests/TerraBase.Slow --configuration Debug --no-build -- \
  --treenode-filter '/*/*/QueryValueRegressionTests/*' --minimum-expected-tests 7
```

## Recorded outcome

Compilation and all seven integration tests are **unrun**. Two bounded attempts failed to acquire `/tmp/ts-heavy.lock`:

| Attempt | Lock wait | Exit | Evidence |
|---|---:|---:|---|
| Initial baseline build | 60 seconds | 1 | `/tmp/query-value-baseline-build.log`: 0 bytes; dotnet never started |
| Focused build with new tests | 600 seconds | 1 | `/tmp/query-value-focused-build.log`: 0 bytes; dotnet never started |

At the initial inspection, one process held the lock and seven processes were waiting. The holder subsequently changed, but this build never acquired it. This is contention evidence, not a source regression or a passing build. No Slow test failure is claimed, so there is no failing-test issue to file.

The composite JSON fixture and all 13 other raw JSON literals were parsed locally with Python; the composite contains the intended 128-digit integer, signed integer bounds and embedded NUL. Both new files pass `git diff --cached --check`. These are fixture/whitespace checks, not C# compilation or integration verification.

Full repository checks and the complete Fast/Slow suites have not run. Shared release CI is contended and no duplicate full build is requested.

A third, single focused build request is queued with a 30-minute admission bound and a 4-minute build bound; its output goes to `/tmp/query-value-focused-build-queued.log`. Earlier waiters from this task have exited; there is no duplicate waiter. Sanitized snapshots showed one holder and five other queued requests before this request joined, then one holder and six queued requests. Holder PID `1950439` (`flock`, state `Ss`, elapsed 1,400 seconds at the second snapshot) advanced from `dotnet`/`TerraBase.Fast` children `2028307`/`2028386` to children `2033319`/`2033414`. This process turnover shows work progressing, but does not establish test success. No environment values or command-line arguments were read for this inspection.

Residual dependency: let release work retain priority, acquire the existing lock, compile the unchanged Slow project, and run the seven filtered tests above. No shared fixture or project edit is needed. The requested two-file draft records pending validation; required checks remain outstanding and no merge is authorized. The tests must remain enabled and any actual regression must be reported without changing its expected invariant.
