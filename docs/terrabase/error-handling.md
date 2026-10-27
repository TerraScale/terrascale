# Error handling

Expected failures (not found, name taken, invalid input, role too low, quota hit, service unavailable) are
**values**, not exceptions. An operation returns a C# 15 `union` of its success type and the failures it can
produce. The caller handles every case in a `switch` expression, and the compiler checks that the switch covers
all of them. One function turns a failure into an HTTP response.

The native `union` feature compiles on the pinned .NET 11 RC1 SDK with `LangVersion` `preview`, so the closed-record
fallback was not needed.

## The pieces

| Where | What |
|-------|------|
| `src/TerraBase/Errors/Errors.cs` | The failure kinds `NotFound`, `Conflict`, `Validation`, `PreconditionFailed` (a stale version, TS-521), `Unauthorized`, `Forbidden`, `QuotaExceeded`, `Unavailable`, `PayloadTooLarge`, `UnsupportedMediaType` (request bodies) and `RequestTimeout` (body reception deadline). Each has a message (the rule that failed, never client input) and a stable `Code`. Also `Done` (success without a value), `Failure` (all kinds), `Valid<T>` (a request value or `Validation`) and `Found<T>` (a resource or `NotFound`). |
| `src/TerraBase/Errors/ErrorResponse.cs` | `ErrorResponse.From(Failure, op)`, the single mapping to HTTP: 404, 409, 400, 412, 401, 403, 429, 503, 413, 415, 408. `op` names the failing operation of a batch (`error.op`), as Management commits do. The body is the existing `{"error":{"code","message"}}` envelope (decision 0004), with `Cache-Control: no-store`. |
| The feature | One `union` per operation that lists exactly its outcomes, for example `public readonly union DeleteProjectResult(Done, NotFound, Conflict);`. It also holds shared instances with feature codes, such as `ManagementErrors.NameTaken` (`name_taken`) and `ManagementErrors.DatabaseLimit` (`409 database_limit`, [plan database allowance](plan-database-allowance.md)). |

`Errors/` is shared from the start, unlike other shared primitives that wait for a second feature: every folder
converts to it, one PR per folder.

Unions do not flatten. A `union Result(Project, Failure)` would hide which failures are possible, so list the cases
in the operation's own union.

## Example

```csharp
// Service: return a case; each case converts implicitly to the union.
public async ValueTask<DeleteProjectResult> DeleteProjectAsync(...) =>
    await WriteAsync<DeleteProjectResult>(async transaction =>
    {
        if (await GetProjectAsync(...) is not { } project) return new NotFound();
        if (await AnyAsync(...)) return ManagementErrors.NotEmpty;
        ...
        return new Done();
    }, cancellationToken);

// Endpoint: validate the request into unions, then switch over every case.
public override async ValueTask<IResult> HandleAsync(CancellationToken cancellationToken) =>
    ManagementRoute.Caller(HttpContext) switch
    {
        Validation error => ErrorResponse.From(error),
        OrganizationCaller caller => await management.DeleteProjectAsync(caller.UserId, ...) switch
        {
            Done => TypedResults.NoContent(),
            NotFound error => ErrorResponse.From(error),
            Conflict error => ErrorResponse.From(error),
        },
    };
```

A union's `default` value has no case and fails every switch at run time, so never return `default` for one.

To validate several inputs, switch over a tuple: `(Validation error, _) => …, (_, Validation error) => …, (A a, B b) => …`.

## Enforcement (the build fails)

1. **Non-exhaustive switch:** CS8509, an error because `TreatWarningsAsErrors` is on. Adding a case to a union
   breaks every caller until it handles the new case.
2. **Discarded result:** TB0001 from `src/TerraBase.Analyzers`, an analyzer-only reference that is not
   in the binary. It fires on a statement that drops a union value, a `Task`/`ValueTask` of one, a `?.` call
   or a `ConfigureAwait` of either, including `await service.DeleteAsync(...);`. Write `_ = …` to discard on purpose.
   `make analyzer-check` compiles the native-union fixture in `src/TerraBase.Analyzers.BuildCheck` and
   checks the expected diagnostics for direct, awaited, configured, nullable, constrained-generic and derived-task
   results; assigned values and explicit `_ =` statements are negative controls.
3. **Throwing expected failures:** `BannedSymbols.txt` forbids every constructor overload of
   `BadHttpRequestException` through RS0030, including aliases and target-typed `new(...)`. The obsolete
   `ApiException` type and its middleware bridge are removed; that type is banned too. The migration
   allowlist and regex build target are gone. Framework-created `BadHttpRequestException` values are still
   caught at the HTTP boundary and mapped to the error envelope. `make analyzer-check` also verifies
   RS0030 for all four overloads through an alias and for target-typed construction; catching the framework
   type is its negative control.

Exceptions remain for bugs and broken invariants (`InvalidOperationException`, `ArgumentOutOfRangeException`,
`UnreachableException`), for startup configuration errors (`TerraBaseConfigurationException`, exit code 2), and
at I/O boundaries. At an I/O boundary, catch the exception where it happens and convert it to a case, for example a
capacity exception to `QuotaExceeded`. `BadRequests` still answers a framework `BadHttpRequestException`, such as a
malformed JSON body, with `400 invalid_request`.

## Converting a feature (one PR per folder)

1. Give each operation its own union. Return cases instead of throwing, and instead of error enums or `null`.
2. Make validators return `Valid<T>`. Make endpoints switch, and send every failure through `ErrorResponse.From`.
   Declare `Produces<ErrorBody>(status)`.
3. Keep the constructor bans in `BannedSymbols.txt`; expected failures must never use an exception bridge.
4. Cover every failure branch in the Fast suite (status, `error.code`, `error.message`), as
   `ManagementErrorTests` does.

## Checklist

`throw new` counts in `src/TerraBase`, excluding generated `obj/` code: 456 (TS-538).
"Expected" counts constructions of `ApiException`/`BadHttpRequestException`; none remain and the compiler bans
their construction. Internal parser aborts, I/O and gRPC boundary exceptions, startup configuration failures and
invariant checks remain as described below.

| Folder | `throw new` | Expected | State |
|--------|------------:|---------:|-------|
| `Features/Management` (orgs, projects) | 0 | 0 | **Converted** (TS-538): each operation returns its native union; validators return `Valid<T>` and endpoints map failures through `ErrorResponse.From`. |
| `Features/Management` commits, versions, dashboard documents (TS-521) | 0 | 0 | **Converted**: `CommitResult(CommitOutcome, CommitRejected)` carries the failing operation's index to `ErrorResponse.From(error, op)`; a document operation's `DocumentRejected` becomes an `OperationFailed` with the same `Failure` |
| `Features/Management/Schema` | 0 | 0 | **Converted** (TS-538): schema inference and collection counts return native unions; engine capacity is converted at the storage call |
| `Features/Management/Backups` | 5 | 0 | **Converted** (TS-538): create, policy and restore return unions; `ManagementRoute.CheckWritableDatabaseAsync` and `ManagementErrors.NameTaken` (`409 name_taken`) are shared. The five throws are invariants |
| `Features/Management/Indexes` | 2 | 0 | **Converted** (TS-538): `IndexErrors.Exists` (`409 index_exists`) and `IndexErrors.DefaultNameTaken` (`409 name_taken`) |
| `Features/Management/Queries` | 43 | 0 | **Converted** (TS-538): `SqlSubset.Parse` returns `Valid<SqlStatement>`; run and check return `invalid_query` past the engine read budget. Kept: 30 `FormatException` aborts inside the SQL subset translator, which `Parse` catches with the parser's own `ParserException`, so none leaves `SqlSubset`; eleven invariants and two gRPC boundary failures |
| `Features/Management` root (access, databases, catalog, recovery, commit invariants) | 61 | 0 | **Converted** (TS-538): membership, invitations, ownership, audit and database operations return native unions. Catalog guards return `Unavailable` for unsupported formats and populated catalogs without a format marker; callers propagate that case through HTTP, commits and peer boundaries. `ManagementError`, `ManagementResult` and throwing validators are removed. Remaining throws protect invariants. |
| `Features/Management/Usage` | 10 | 0 | **Converted** (TS-538): usage reads carry `Validation` and `Unavailable`; capacity failures are caught inside the service and use `400 invalid_query` and peer failures use gRPC status. Remaining throws are local budget aborts, boundary failures and invariants |
| `Features/Documents` request edge (`DocumentRoute`, `RequestJson`, `ETags`, `DocumentPatch` parsing, the document, collection and database endpoints) | 2 | 0 | **Converted** (TS-538): `Check…` helpers return `TenantResult`, `KeyResult`, `BodyResult`, `Valid<T>`, `NotModifiedResult` and `VersionCheck`, and `DocumentPatch.CheckMerge`/`CheckOps` return `Valid<DocumentPatch>`; endpoints switch over them. The `DocumentRoute.Throwable` bridge and its throwing wrappers are gone. Kept: `RequestJson`'s internal `JsonException` for a null list element, which its own reader catches, and one invariant |
| `Features/Documents/QueryParser` | 3 | 0 | **Converted** (TS-538): `CheckParse`, `CheckFilter`, `CheckCursor` and `CheckPosition` return `Valid<T>`. As in `SqlSubset`, the recursive filter translation rejects a node with a `FormatException` that only those entry points catch, each answering with its own code, so none leaves the class; `Position` keeps its own `FormatException` to fall through to `invalid_cursor` |
| `Features/Documents` service, transactions and patch application (`DocumentService`, `TransactionEndpoint`, `DocumentPatch.Apply`, `DocumentCache`) | 4 | 0 | **Converted** (TS-538): `IDocumentService` returns `CollectionResult`, `Found<CollectionPage>`, `DocumentRead`, `QueryResult`, `WriteOutcome` and `ApplyOutcome`; a failed write is `DocumentRejected(Failure, Op)`, which a transaction answers with `error.op`. `DocumentPatch.Apply` returns `PatchOutcome(Done, Validation, Conflict)`. Engine capacity, write-limit and document-validation exceptions are caught where the service calls the engine. Four invariants |
| `Features/Replication` | 93 | 0 | **Converted** (TS-538): eligibility failures propagate as unions; unknown registration retains files and replication progress. Kept at the peer gRPC boundary (TS-464): `RpcException` status for peers, `InvalidDataException` for a protocol violation that ends the stream, and invariants |
| `Features/ApiKeys` | 4 | 0 | **Converted** (TS-538): authentication lookup, issue and project-key operations return native unions; request validation uses `ErrorResponse.From`. Remaining throws are invariants |
| `Features/UserAuth` | 5 | 0 | **Converted** (TS-538): refresh returns cached signing keys or `Unavailable`; framework authentication results remain at the authentication boundary |
| `Engine` | 108 | 0 | Keep: I/O, capacity and validation exceptions are converted where feature services call the engine; other throws protect engine invariants |
| `Configuration` | 54 | 0 | Keep: startup configuration errors, exit code 2 |
| `Program.cs` | 1 | 0 | Keep: startup configuration error |
| `Hosting` | 1 | 0 | Keep: URI construction invariant |
| `Features/Backup` | 24 | 0 | **Converted** (TS-538): restore windows return unions and unknown plans skip retention; remaining exceptions are backup I/O, protocol validation and invariants |
| `Features/CrossRegion` | 25 | 0 | **Converted** (TS-538): adoption, placement and WAN results use unions; unavailable catalogs keep deletion retries pending. Remaining exceptions are peer protocol, I/O and invariants |
| `Primitives` | 8 | 0 | Keep: invariant and resource ownership checks |

`Tenancy/ITenantDirectory` also uses native unions: an unreadable catalog is `Unavailable`, never a negative registration, an empty inventory or a Hobby plan. Known missing organizations retain their documented Hobby default.
