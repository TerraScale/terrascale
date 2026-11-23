using TerraBase.Features.Callers;
using TerraBase.Hosting;
using TerraScale.Endpoints;

namespace TerraBase.Features.Rules;

public readonly union RulesWriteResponse(RulesResponse, InvalidRulesResponse, StaleRulesResponse, RulesPreconditionRequiredResponse, Validation, Unauthorized, Forbidden, NotFound, Unavailable);

internal sealed class PutRulesEndpoint(RulesService rules) : Endpoint<RulesInput, RulesWriteResponse>
{
    public override void Configure()
    {
        Put(RulesRoute.Rules);
        Name("putDatabaseRules");
        Summary("Deploys a database's rules");
        Description("Send If-Match: \"<current version>\", or If-None-Match: * when the database has no rules; a missing precondition answers 428 precondition_required and a stale version answers 412 version_mismatch. "
            + "Every condition is compiled against the current schema; problems answer 400 invalid_rules with details.problems.");
        Tags("Rules");
        AllowAnonymous();
        Metadata(ListenerMetadata.Management);
    }

    public override async ValueTask<RulesWriteResponse> HandleAsync(RulesInput request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Tables is null || request.Tables.Values.Any(static table => table is null))
        {
            return new Validation("the body is {\"tables\": {\"<table>\": {...}}}", "invalid_rules");
        }

        return RulesWrites.Map(await rules.DeployAsync(HttpContext.Caller, request.Project, request.Database, new RulesSet(request.Tables),
            RulesRoute.IfMatch(HttpContext.Request), RulesRoute.IfNoneMatchAny(HttpContext.Request), cancellationToken));
    }
}

internal static class RulesWrites
{
    public static RulesWriteResponse Map(RulesDeployResponse response) => response switch
    {
        DeployedRules deployed => new RulesResponse(deployed.Rules, [], deployed.Warnings),
        InvalidRules invalid => new InvalidRulesResponse(invalid),
        StaleRules stale => new StaleRulesResponse(stale),
        RulesPreconditionRequired required => new RulesPreconditionRequiredResponse(required),
        Validation invalid => invalid,
        Unauthorized unauthorized => unauthorized,
        Forbidden forbidden => forbidden,
        NotFound missing => missing,
        Unavailable unavailable => unavailable,
    };
}
