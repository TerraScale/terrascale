using System.Text.Json;
using TerraBase.Errors;
using TerraBase.Features.ApiKeys;
using TerraBase.Features.Management;
using TerraBase.Hosting;
using TerraBase.Tenancy;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal sealed class TestRulesEndpoint(IManagementService management, IRules rules, TimeProvider time) : Endpoint<RulesTestRequest, IResult>
{
    public override void Configure()
    {
        Post(RulesRoute.Rules + ":test");
        Name("testDatabaseRules");
        Summary("Simulates one request against deployed or draft rules");
        Description("Evaluates rules for exactly the supplied values without reading or writing application rows. "
            + "For update, new is a patch merged over row before set.update; create applies set.create. "
            + "Reports the deciding clause, evaluated conditions and readable columns. Draft rules receive the deploy checks; "
            + "problems answer 200 with allowed false. Defaults, affinity and triggers are not simulated. Runs in any region. "
            + RulesRoute.Description);
        Tags("Rules");
        Produces<RulesTestBody>();
        Produces<ErrorBody>(StatusCodes.Status400BadRequest);
        Produces<ErrorBody>(StatusCodes.Status403Forbidden);
        Produces<ErrorBody>(StatusCodes.Status404NotFound);
        Produces<ErrorBody>(StatusCodes.Status503ServiceUnavailable);
        RequireAuthorization(ApiKeyScopes.DatabasesRead);
        Metadata(new ProjectManagementRoute(), ListenerMetadata.Management, new DatabaseRegions.ReplicaReads());
    }

    public override async ValueTask<IResult> HandleAsync(RulesTestRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Operation is not ("read" or "create" or "update" or "delete") || string.IsNullOrWhiteSpace(request.Table)
            || request.Auth is null || string.IsNullOrWhiteSpace(request.Auth.Uid) || !Row(request.Row) || !Row(request.New)
            || request.Rules is { Tables: null } || request.Rules?.Tables.Values.Any(rule => rule is null) == true)
        {
            return ErrorResponse.From(new Validation("send read, create, update or delete, a table, auth.uid and scalar row and new values"));
        }

        var resolved = await RulesRoute.DatabaseAsync(HttpContext, management, cancellationToken);
        if (resolved is not TenantContext database)
        {
            return (IResult)resolved;
        }

        HttpContext.Response.Headers.CacheControl = "no-store";
        return await rules.TestAsync(database, request with { Time = request.Time ?? time.GetUtcNow().ToUnixTimeMilliseconds() }, cancellationToken) switch
        {
            RulesTestBody body => TypedResults.Json(body, RulesJsonContext.Default.RulesTestBody),
            NotFound error => ErrorResponse.From(error),
            Unavailable error => ErrorResponse.From(error),
        };
    }

    private static bool Row(IReadOnlyDictionary<string, JsonElement>? values) => values is null
        || values.Count <= RulesChecks.MaxColumns
        && values.Keys.Distinct(StringComparer.OrdinalIgnoreCase).Count() == values.Count
        && values.Values.All(value => value.ValueKind is JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False or JsonValueKind.Null);
}
