using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

// Feature-scoped DashboardStore extensions for Access rules.
public sealed partial class DashboardStore
{
    public const string DefaultAccessRuleSource = "principal.id == document.ownerId";
    public const string DefaultAccessRulePrincipal = "{\n  \"id\": \"user_123\",\n  \"role\": \"member\"\n}";
    public const string DefaultAccessRuleDocument = "{\n  \"ownerId\": \"user_123\",\n  \"visibility\": \"private\"\n}";

    public ImmutableArray<AccessRule> AccessRules { get => World.AccessRules; private set => World.AccessRules = value; }

    public AccessRuleValidationState? LastAccessRuleValidation { get => World.LastAccessRuleValidation; set => World.LastAccessRuleValidation = value; }
    public AccessRuleDryRunState? LastAccessRuleDryRun { get => World.LastAccessRuleDryRun; set => World.LastAccessRuleDryRun = value; }

    public AccessRule? FindAccessRule(string id) => AccessRules.FirstOrDefault(r => r.Id == id);

    public void SaveAccessRule(string id, string name, string source)
    {
        if (id == "new" || FindAccessRule(id) is null)
            AccessRules = AccessRules.Add(new($"rule_{Guid.NewGuid():N}"[..20], name, source, "local-draft", "local"));
        else
            AccessRules = [.. AccessRules.Select(r => r.Id == id ? r with { Name = name, Source = source } : r)];
    }

    public void DeleteAccessRule(string id) =>
        AccessRules = [.. AccessRules.Where(r => r.Id != id)];
}
