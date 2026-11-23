using System.Collections.Frozen;
using System.Diagnostics;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Features.Sql;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal sealed class TestRulesEndpoint(RulesService rules, RulesStore store, DatabaseAccess access, TimeProvider time)
    : Endpoint<RulesTestInput, Result<RulesTestBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>>
{
    public override void Configure()
    {
        Post(RulesRoute.Rules + ":test");
        Name("testDatabaseRules");
        Summary("Evaluates a rule for a simulated end user");
        Description("{\"tables\"?, \"auth\": {\"uid\", \"claims\", \"orgId\", \"orgRole\", \"isAnonymous\"}, \"table\", \"operation\": read|create|update|delete, "
            + "\"row\"?, \"new\"?}: evaluates the deployed rules (or the given tables) against the given row values without touching data.");
        Tags("Rules");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<Result<RulesTestBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> HandleAsync(
        RulesTestInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var operation = request.Operation switch
        {
            "read" => RulesOperation.Read,
            "create" => RulesOperation.Create,
            "update" => RulesOperation.Update,
            "delete" => RulesOperation.Delete,
            _ => (RulesOperation?)null,
        };
        if (operation is not { } op || request.Table is not { Length: > 0 } table || request.Auth is not { Uid: { Length: > 0 } uid } auth)
        {
            return new Validation("table, operation (read, create, update or delete) and auth.uid are required", "invalid_request");
        }

        return await rules.ResolveAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken) switch
        {
            ResolvedDatabase resolved => await access.OpenAsync(resolved.Database, cancellationToken) switch
            {
                ISqlDatabaseSession session => request.Tables is { } tables
                    ? await EvaluateAsync(session, new RulesSet(tables), table, op, Auth(uid, auth), request, cancellationToken)
                    : await store.CurrentAsync(resolved.Database.Id, cancellationToken) switch
                    {
                        StoredRules current => await EvaluateAsync(session, current.Rules, table, op, Auth(uid, auth), request, cancellationToken),
                        NotFound => new RulesTestBody(false, null, []),
                        Unavailable unavailable => unavailable,
                    },
                Unavailable unavailable => unavailable,
            },
            Validation invalid => invalid,
            Unauthorized unauthorized => unauthorized,
            Forbidden forbidden => forbidden,
            NotFound missing => missing,
            Unavailable unavailable => unavailable,
        };
    }

    private static RulesAuth Auth(string uid, RulesTestAuth auth)
    {
        var claims = auth.Claims is { ValueKind: JsonValueKind.Object } ? RulesAuth.Read(uid, Wrap(auth.Claims.Value), custom: false)?.Claims : null;
        return new RulesAuth(uid, claims ?? FrozenDictionary<string, RulesClaim>.Empty, auth.OrgId, auth.OrgRole, auth.IsAnonymous);
    }

    private static JsonElement Wrap(JsonElement claims)
    {
        using var document = JsonDocument.Parse(string.Concat("{\"claims\":", claims.GetRawText(), "}"));
        return document.RootElement.Clone();
    }

    private async ValueTask<Result<RulesTestBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> EvaluateAsync(
        ISqlDatabaseSession session, RulesSet set, string table, RulesOperation operation, RulesAuth auth, RulesTestInput request, CancellationToken cancellationToken)
    {
        var rule = set.Table(table);
        var condition = operation switch
        {
            RulesOperation.Read => rule?.Read,
            RulesOperation.Create => rule?.Create,
            RulesOperation.Update => rule?.Update,
            RulesOperation.Delete => rule?.Delete,
            RulesOperation.Validate => null,
            _ => throw new UnreachableException(),
        };
        if (condition is null)
        {
            return new RulesTestBody(false, null, []);
        }

        var columns = await RulesService.ColumnsAsync(session, table, cancellationToken);
        return RulesCompiler.Compile(condition, columns, operation) switch
        {
            CompiledCondition compiled => await RunAsync(session, columns, compiled, auth, request, cancellationToken),
            Validation refused => new RulesTestBody(false, condition, [new RulesProblem(table, "condition", refused.Code, refused.Message)]),
        };
    }

    private async ValueTask<Result<RulesTestBody, Validation, Unauthorized, Forbidden, NotFound, Unavailable>> RunAsync(
        ISqlDatabaseSession session, IReadOnlyList<string> columns, CompiledCondition compiled, RulesAuth auth, RulesTestInput request, CancellationToken cancellationToken)
    {
        var row = string.Join(", ", columns.Select(column => string.Concat(Literal(request.Row, column), " AS ", RulesCompiler.Quote(column))));
        var next = string.Join(", ", columns.Select(column => string.Concat(Literal(request.New, column), " AS ", RulesCompiler.Quote(column))));
        var sql = string.Concat("SELECT ", RulesCompiler.Render(compiled, auth, time.GetUtcNow(), "new.", "row_values."), " FROM (SELECT ", row, ") AS row_values, (SELECT ", next, ") AS new");
        var statement = new SqlStatementRequest(session.DatabaseId, [SqlBuild.Statement(sql)], SqlAccess.Read, ConsistencyMode.Fast, CausalBookmark.Empty);
        return await session.ExecuteReadAsync(statement, cancellationToken) switch
        {
            SqlReadResult { ResultSets: [.., { Rows: [[var allowed, ..], ..] }] } => new RulesTestBody(allowed.TryGetIntegral(out var value) && value != 0, sql, []),
            SqlReadResult => new RulesTestBody(false, sql, []),
            SqlError error => new RulesTestBody(false, sql, [new RulesProblem("", "condition", "sql_error", error.Message)]),
            StorageBusy busy => new Unavailable(busy.Message, "storage_busy"),
            RequestTimeout timeout => new Unavailable(timeout.Message, "request_timeout"),
        };
    }

    private static string Literal(IReadOnlyDictionary<string, JsonElement>? values, string column) =>
        values is not null && values.FirstOrDefault(pair => string.Equals(pair.Key, column, StringComparison.OrdinalIgnoreCase)) is { Key: not null } found
            && SqlWire.Value(found.Value) is { } value
            ? value.Kind switch
            {
                SqlValueKind.Text => RulesCompiler.Literal(SqlBuild.Format(value)),
                SqlValueKind.Null => "NULL",
                SqlValueKind.Blob => string.Concat("X'", value.TryGetBlob(out var blob) ? Convert.ToHexString(blob.Span) : "", "'"),
                SqlValueKind.Integral or SqlValueKind.Real => SqlBuild.Format(value),
                _ => throw new UnreachableException(),
            }
            : "NULL";
}
