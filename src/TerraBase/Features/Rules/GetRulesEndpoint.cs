using TerraBase.Features.Callers;
using TerraBase.Features.Catalog;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

internal readonly union RulesReadResponse(RulesResponse, InvalidInput, CredentialRejected, CallerForbidden, ResourceNotFound, BackendUnavailable);

internal sealed class GetRulesEndpoint(RulesService rules, RulesStore store, DatabaseAccess access)
    : Endpoint<RulesRouteInput, RulesReadResponse>
{
    public override void Configure()
    {
        Get(RulesRoute.Rules);
        Name("getDatabaseRules");
        Summary("Reads a database's rules");
        Description("Answers the deployed rules with ETag: \"<version>\"; problems lists rules that no longer compile against the schema. 404 no_rules when none were deployed.");
        Tags("Rules");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<RulesReadResponse> HandleAsync(
        RulesRouteInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var outcome = await rules.ResolveAsync(HttpContext.Caller, request.Project, request.Database, Scopes.DatabasesRead, cancellationToken);
        return outcome switch
        {
            ResolvedDatabase resolved => await store.CurrentAsync(resolved.Database.Id, fresh: true, cancellationToken) switch
            {
                StoredRules current => await access.OpenAsync(resolved.Database, cancellationToken) switch
                {
                    ISqlDatabaseSession session => new RulesResponse(current, await RulesService.ProblemsAsync(session, current.Rules, cancellationToken),
                        RulesService.Warnings(current.Rules)),
                    BackendUnavailable unavailable => unavailable,
                },
                ResourceNotFound missing => missing,
                BackendUnavailable unavailable => unavailable,
            },
            InvalidInput invalid => invalid,
            CredentialRejected unauthorized => unauthorized,
            CallerForbidden forbidden => forbidden,
            ResourceNotFound missing => missing,
            BackendUnavailable unavailable => unavailable,
        };
    }
}

public sealed class RulesResponse(StoredRules rules, IReadOnlyList<RulesProblem> problems, IReadOnlyList<RulesProblem> warnings) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext) => RulesRoute.Body(httpContext, rules, problems, warnings).ExecuteAsync(httpContext);
}

public sealed class InvalidRulesResponse(InvalidRules invalid) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext) => RulesRoute.Invalid(invalid).ExecuteAsync(httpContext);
}

public sealed class RulesPreconditionRequiredResponse(RulesPreconditionRequired required) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext) =>
        ErrorResults.Json(StatusCodes.Status428PreconditionRequired, "precondition_required", required.Message).ExecuteAsync(httpContext);
}

public sealed class StaleRulesResponse(StaleRules stale) : IResult
{
    public Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (stale.Current is { } current)
        {
            httpContext.Response.Headers.ETag = string.Concat("\"", current, "\"");
        }

        return ErrorResults.Json(StatusCodes.Status412PreconditionFailed, "version_mismatch", "the rules changed since the version you named; read them again")
            .ExecuteAsync(httpContext);
    }
}
