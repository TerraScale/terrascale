using System.Collections.Immutable;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.AccessRules;

/// <summary>Live validate: runs the local CEL compiler over the edited source
/// and reports the parser diagnostics (line/column/message) plus index
/// analysis. Does not persist anything.</summary>
public sealed class ValidateAccessRulesEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/access-rules/validate");
        Summary("Validates a CEL access rule locally");
        Tags("AccessRules");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var ruleId = form["ruleId"].ToString();
        var name = form["name"].ToString().Trim();
        var source = form["source"].ToString();
        var compilation = AccessRuleCompiler.Compile(source);

        store.LastAccessRuleValidation = new AccessRuleValidationState(
            ruleId, name, source, compilation.Ok, compilation.Diagnostics, compilation.Indexes);
        return Results.Redirect(
            "/access-rules?validate=1&edit=" + Uri.EscapeDataString(ruleId.Length > 0 ? ruleId : "new"), false, false);
    }
}
