using System.Text.Json;
using TerraScale.Dashboard2;
using TerraScale.Endpoints;

namespace TerraScale.Dashboard2.Features.AccessRules;

/// <summary>Dry-run evaluation: compiles the selected rule and evaluates it
/// against the posted principal/document JSON and authorization mode
/// (evaluateAccessRuleAction parity). Never enforces anything.</summary>
public sealed class DryRunAccessRuleEndpoint(DashboardStore store) : EndpointWithoutRequest<IResult>
{
    public override void Configure()
    {
        Post("/api/v1/access-rules/dry-run");
        Summary("Dry-runs a CEL access rule against a local context");
        Tags("AccessRules");
    }

    public override async Task<IResult> HandleAsync(CancellationToken ct)
    {
        var form = await HttpContext.Request.ReadFormAsync(ct);
        var ruleId = form["ruleId"].ToString();
        var principalJson = form["principal"].ToString();
        var documentJson = form["document"].ToString();
        var authMode = form["authMode"].ToString();

        string? error = null;
        AccessRuleEvaluation? result = null;
        var rule = store.FindAccessRule(ruleId);
        if (rule is null)
            error = "Choose a rule to evaluate.";
        else if (authMode is not ("publishable-key" or "byo-jwt"))
            error = "Choose a supported authorization context.";
        else if (!TryReadObject(principalJson, out var principal) || !TryReadObject(documentJson, out var document))
            error = "Principal and document must be JSON objects.";
        else
            result = AccessRuleCompiler.Evaluate(rule.Source, new AccessRuleEvaluationContext(
                principal,
                document,
                JsonSerializer.SerializeToElement(new { mode = authMode })));

        store.LastAccessRuleDryRun = new AccessRuleDryRunState(
            ruleId, principalJson, documentJson, authMode, result, error);
        return Results.Redirect("/access-rules?dryrun=1", false, false);
    }

    private static bool TryReadObject(string json, out JsonElement value)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                value = default;
                return false;
            }
            value = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            value = default;
            return false;
        }
    }
}
