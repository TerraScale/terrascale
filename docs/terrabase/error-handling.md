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
| `src/TerraScale.TerraBase/Errors/Errors.cs` | The failure kinds `NotFound`, `Conflict`, `Validation`, `PreconditionFailed` (a stale version, TS-521), `Unauthorized`, `Forbidden`, `QuotaExceeded`, `Unavailable`, `UriTooLong` (a shape filter whose continuation URL would not fit), `PayloadTooLarge` and `UnsupportedMediaType` (request bodies). Each has a message (the rule that failed, never client input) and a stable `Code`. Also `Done` (success without a value), `Failure` (all kinds), `Valid<T>` (a request value or `Validation`) and `Found<T>` (a resource or `NotFound`). |
| `src/TerraScale.TerraBase/Errors/ErrorResponse.cs` | `ErrorResponse.From(Failure, op)`, the single mapping to HTTP: 404, 409, 400, 412, 401, 403, 429, 503, 414, 413, 415. `op` names the failing operation of a batch (`error.op`), as Management commits do. The body is the existing `{"error":{"code","message"}}` envelope (decision 0004), with `Cache-Control: no-store`. |
| The feature | One `union` per operation that lists exactly its outcomes, for example `public readonly union DeleteProjectResult(Done, NotFound, Conflict);`. It also holds shared instances with feature codes, such as `ManagementErrors.NameTaken` (`name_taken`). |

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
2. **Discarded result:** TB0001 from `src/TerraScale.TerraBase.Analyzers`, an analyzer-only reference that is not
   in the binary. It fires on a statement that drops a union value, a `Task`/`ValueTask` of one, a `?.` call
   or a `ConfigureAwait` of either, including `await service.DeleteAsync(...);`. Write `_ = …` to discard on purpose.
   `make analyzer-check` compiles the native-union fixture in `src/TerraScale.TerraBase.Analyzers.BuildCheck` and
   checks the expected diagnostics for direct, awaited, configured, nullable, constrained-generic and derived-task
   results; assigned values and explicit `_ =` statements are negative controls.
3. **Throwing expected failures:** the `BanExpectedFailureExceptions` target in `Directory.Build.targets` fails
   the build when code constructs `ApiException` or `BadHttpRequestException` outside the files that still do so
   (listed in `ExpectedFailureExceptionFiles`). Its pattern matches `new ApiException(`, not a target-typed `new(…)`
   returned as one: write the type out in the files that still construct it. The final conversion replaces the target
   with `BannedSymbols.txt`, which sees every form.

Exceptions remain for bugs and broken invariants (`InvalidOperationException`, `ArgumentOutOfRangeException`,
`UnreachableException`), for startup configuration errors (`TerraBaseConfigurationException`, exit code 2), and
at I/O boundaries. At an I/O boundary, catch the exception where it happens and convert it to a case, for example a
capacity exception to `QuotaExceeded`. `BadRequests` still answers a framework `BadHttpRequestException`, such as a
malformed JSON body, with `400 invalid_request`.

## Converting a feature (one PR per folder)

1. Give each operation its own union. Return cases instead of throwing, and instead of error enums or `null`.
2. Make validators return `Valid<T>`. Make endpoints switch, and send every failure through `ErrorResponse.From`.
   Declare `Produces<ErrorBody>(status)`.
3. Remove the folder's files from `ExpectedFailureExceptionFiles`. After the last one, move both constructors to
   `BannedSymbols.txt` and delete the target.
4. Cover every failure branch in the Fast suite (status, `error.code`, `error.message`), as
   `ManagementErrorTests` does.

## Checklist

`throw new` counts in `src/TerraScale.TerraBase`, excluding generated `obj/` code: 295 (TS-538 recounted them
after TS-463, TS-464, TS-521, TS-473, TS-561, TS-574 and the Documents conversion; TS-518's figure of 127 predates them). "Expected" counts the
`ApiException`/`BadHttpRequestException` constructions that `BanExpectedFailureExceptions` matches. The other throws
in each row are invariants or configuration errors to review during the conversion.

| Folder | `throw new` | Expected | State |
|--------|------------:|---------:|-------|
| `Features/Management` (orgs, projects) | 0 | 0 | **Not converted on `main`.** TS-518's reference conversion (commit `84b6ebd`) was lost when it was rebased onto TS-463; its endpoints still go through `ManagementRoute.Error` and the throwing validators. |
| `Features/Management` commits, versions, dashboard documents (TS-521) | 0 | 0 | **Converted**: `CommitResult(CommitOutcome, CommitRejected)` carries the failing operation's index to `ErrorResponse.From(error, op)`; a document operation's `DocumentRejected` becomes an `OperationFailed` with the same `Failure` |
| `Features/Management/Schema` | 0 | 0 | **Converted** (TS-538) |
| `Features/Management/Credentials` | 0 | 0 | **Converted** (TS-538): `IApiKeyStore` project-key operations return unions; a viewer's change is `ManagementErrors.RoleTooLow` (`403 forbidden_role`) |
| `Features/Management/Backups` | 2 | 0 | **Converted** (TS-538): create, policy and restore return unions; `ManagementRoute.CheckWritableDatabaseAsync` and `ManagementErrors.NameTaken` (`409 name_taken`) are shared. The two throws are invariants |
| `Features/Management/Indexes` | 0 | 0 | **Converted** (TS-538): `IndexErrors.Exists` (`409 index_exists`) and `IndexErrors.DefaultNameTaken` (`409 name_taken`) |
| `Features/Management/Queries` | 32 | 0 | **Converted** (TS-538): `SqlSubset.Parse` returns `Valid<SqlStatement>`; run and check return `invalid_query` past the engine read budget. Kept: 31 `FormatException` aborts inside the SQL subset translator, which `Parse` catches with the parser's own `ParserException`, so none leaves `SqlSubset`; one invariant |
| `Features/Management` root (access, databases, catalog, recovery, commit invariants) | 24 | 7 | `ManagementError`/`ManagementResult` and throwing validators remain. The catalog version guard in `EnsureCatalogAsync` still throws `503 unavailable` under every operation, orgs and projects included; it becomes an `Unavailable` case when access and databases convert. |
| `Features/Management/Usage` | 2 | 0 | Review: `QueryCapacityException` thrown and caught inside the folder |
| `Features/Documents` shapes (`GetShapeEndpoint`, `ShapeService`) | 1 | 0 | **Converted** (TS-538): `ShapeReadResult(ShapeResult, NotFound, Validation, QuotaExceeded)`; a collapsed read shares its union result, and only unexpected failures travel as exceptions. `must_refetch` stays a 409 result with the current handle. One invariant |
| `Features/Documents` request edge (`DocumentRoute`, `RequestJson`, `ETags`, `DocumentPatch` parsing, the document, collection and database endpoints) | 2 | 0 | **Converted** (TS-538): `Check…` helpers return `TenantResult`, `KeyResult`, `BodyResult`, `Valid<T>`, `NotModifiedResult` and `VersionCheck`, and `DocumentPatch.CheckMerge`/`CheckOps` return `Valid<DocumentPatch>`; endpoints switch over them. The `DocumentRoute.Throwable` bridge and its throwing wrappers are gone. Kept: `RequestJson`'s internal `JsonException` for a null list element, which its own reader catches, and one invariant |
| `Features/Documents/QueryParser` | 3 | 0 | **Converted** (TS-538): `CheckParse`, `CheckFilter`, `CheckCursor` and `CheckPosition` return `Valid<T>`. As in `SqlSubset`, the recursive filter translation rejects a node with a `FormatException` that only those entry points catch, each answering with its own code, so none leaves the class; `Position` keeps its own `FormatException` to fall through to `invalid_cursor` |
| `Features/Documents` service, transactions and patch application (`DocumentService`, `TransactionEndpoint`, `DocumentPatch.Apply`, `DocumentCache`) | 4 | 0 | **Converted** (TS-538): `IDocumentService` returns `CollectionResult`, `Found<CollectionPage>`, `DocumentRead`, `QueryResult`, `WriteOutcome` and `ApplyOutcome`; a failed write is `DocumentRejected(Failure, Op)`, which a transaction answers with `error.op`. `DocumentPatch.Apply` returns `PatchOutcome(Done, Validation, Conflict)`. Engine capacity, write-limit and document-validation exceptions are caught where the service calls the engine. Four invariants |
| `Features/Replication` | 87 | 0 | Keep (TS-464): peer gRPC boundary only, no HTTP failures. `RpcException` status for peers, `InvalidDataException` for a protocol violation that ends the stream, and invariants |
| `Features/ApiKeys` | 2 | 0 | Review: request validation returns `TypedResults.BadRequest`; the throws are invariants |
| `Features/UserAuth` | 2 | 0 | Review |
| `Engine` | 85 | 0 | Review: `QueryCapacityException`, `StorageWriteLimitException` and `FormatException` cross into Documents |
| `Configuration` | 33 | 0 | Keep: startup configuration errors, exit code 2 |
| `Program.cs` | 1 | 0 | Keep: startup configuration error |
