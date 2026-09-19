using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.AccessRules;

/// <summary>Publish-style save: rejects rules whose CEL source does not compile,
/// surfacing the first parser diagnostic (publishAccessRuleAction parity).</summary>
public sealed class SaveAccessRuleEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/access-rules");
        Summary("Saves an access rule after local compilation");
        Tags("AccessRules");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var ruleId = form["ruleId"].ToString();
        var name = form["name"].ToString().Trim();
        var source = form["source"].ToString();
        var compilation = AccessRuleCompiler.Compile(source);

        if (name.Length == 0)
            return RedirectWithState(ruleId, source, name, compilation, "A rule name is required.");

        if (!compilation.Ok)
            return RedirectWithState(ruleId, source, name, compilation,
                compilation.Diagnostics.IsEmpty ? "The rule is invalid." : compilation.Diagnostics[0].Message);

        store.SaveAccessRule(ruleId, name, source);
        store.LastAccessRuleValidation = null;
        return Results.Redirect("/access-rules", false, false);

        IResult RedirectWithState(string id, string src, string ruleName,
            AccessRuleCompileResult result, string message)
        {
            store.LastAccessRuleValidation = new(id, ruleName, src, result.Ok, result.Diagnostics, result.Indexes);
            return Results.Redirect(
                "/access-rules?validate=1&edit=" + Uri.EscapeDataString(id) +
                "&error=" + Uri.EscapeDataString(message), false, false);
        }
    }
}
