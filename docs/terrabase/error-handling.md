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
| `src/TerraScale.TerraBase/Errors/Errors.cs` | The failure kinds `NotFound`, `Conflict`, `Validation`, `Unauthorized`, `Forbidden`, `QuotaExceeded`, `Unavailable`. Each has a message (the rule that failed, never client input) and a stable `Code`. Also `Done` (success without a value), `Failure` (all kinds), `Valid<T>` (a request value or `Validation`) and `Found<T>` (a resource or `NotFound`). |
| `src/TerraScale.TerraBase/Errors/ErrorResponse.cs` | `ErrorResponse.From(Failure)`, the single mapping to HTTP: 404, 409, 400, 401, 403, 429, 503. The body is the existing `{"error":{"code","message"}}` envelope (decision 0004), with `Cache-Control: no-store`. |
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
   (listed in `ExpectedFailureExceptionFiles`).

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

`throw new` counts in `src/TerraScale.TerraBase` after this change: 127, excluding generated `obj/` code.
"Expected" counts the `ApiException`/`BadHttpRequestException` constructions that `BanExpectedFailureExceptions`
matches. The other throws in each row are invariants or configuration errors to review during the conversion.

| Folder | `throw new` | Expected | State |
|--------|------------:|---------:|-------|
| `Features/Management` (orgs, projects) | 0 | 0 | **Converted** (reference) |
| `Features/Management` (access, databases, catalog, recovery) | 18 | 9 | `ManagementError`/`ManagementResult` and throwing validators remain. The catalog version guard in `EnsureCatalogAsync` still throws `503 unavailable` under every operation, orgs and projects included; it becomes an `Unavailable` case when access and databases convert. |
| `Features/Documents` | 59 | 56 | To do (`ApiException` throughout) |
| `Features/Replication` | 4 | 0 | Review: `RpcException` and `InvalidDataException` at the gRPC boundary |
| `Features/ApiKeys` | 1 | 0 | Review: request validation returns `TypedResults.BadRequest`; the throw is an invariant |
| `Engine` | 29 | 0 | Review: `QueryCapacityException`, `StorageWriteLimitException` and `FormatException` cross into Documents |
| `Configuration` | 15 | 0 | Keep: startup configuration errors, exit code 2 |
| `Program.cs` | 1 | 0 | Keep: startup configuration error |
