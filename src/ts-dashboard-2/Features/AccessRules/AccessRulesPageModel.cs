using TerraScale.Dashboard2;

namespace TerraScale.Dashboard2.Features.AccessRules;

public sealed record AccessRulesPageModel(
    IReadOnlyList<AccessRuleView> Rules,
    int ValidCount,
    string Revision,
    string EntitlementEpoch,
    string? EditRuleId,                 // ?edit=<id|new>
    string EditName,
    string EditSource,
    string EditGeneration,              // "local-draft:local" or "" for unsaved rules
    AccessRuleValidationView? Validation,
    AccessRuleDryRunView? DryRun,
    string? Error = null) : IPageModel
{
    public ShellModel Shell { get; init; } = null!;

    public static AccessRulesPageModel From(
        DashboardStore store, string? edit, bool validate, bool dryRun, string? error)
    {
        var rules = store.AccessRules.Select(rule =>
        {
            var compilation = AccessRuleCompiler.Compile(rule.Source);
            return new AccessRuleView(
                rule.Id, rule.Name, rule.Source, rule.Generation,
                compilation.Ok, compilation.Diagnostics.Length,
                [.. compilation.Indexes.ReferencedFields]);
        }).ToList();

        var lastValidation = validate ? store.LastAccessRuleValidation : null;
        string editRuleId = "", editName = "", editSource = "";
        if (lastValidation is not null)
        {
            (editRuleId, editName, editSource) = (lastValidation.RuleId, lastValidation.Name, lastValidation.Source);
        }
        else if (edit == "new")
        {
            (editRuleId, editSource) = ("new", DashboardStore.DefaultAccessRuleSource);
        }
        else if (edit is { Length: > 0 } && store.FindAccessRule(edit) is { } rule)
        {
            (editRuleId, editName, editSource) = (rule.Id, rule.Name, rule.Source);
        }

        AccessRuleDryRunView? dryRunView = null;
        if (dryRun && store.LastAccessRuleDryRun is { } state)
            dryRunView = new AccessRuleDryRunView(
                state.RuleId, state.PrincipalJson, state.DocumentJson, state.AuthMode, state.Result, state.Error);

        var seed = store.AccessRules.FirstOrDefault();
        return new AccessRulesPageModel(
            rules,
            rules.Count(r => r.IsValid),
            seed?.Revision ?? "local-draft",
            seed?.EntitlementEpoch ?? "local",
            editRuleId,
            editName,
            editSource,
            store.FindAccessRule(editRuleId)?.Generation ?? "",
            lastValidation is null ? null : new AccessRuleValidationView(
                lastValidation.Ok,
                lastValidation.Diagnostics,
                [.. lastValidation.Indexes.Required],
                [.. lastValidation.Indexes.Recommended]),
            dryRunView,
            error);
    }
}
