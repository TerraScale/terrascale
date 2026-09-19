using System.Collections.Immutable;

namespace TerraScale.Dashboard2;

// Feature-scoped DashboardStore extensions for Access rules.
public sealed partial class DashboardStore
{
    public const string DefaultAccessRuleSource = "principal.id == document.ownerId";
    public const string DefaultAccessRulePrincipal = "{\n  \"id\": \"user_123\",\n  \"role\": \"member\"\n}";
    public const string DefaultAccessRuleDocument = "{\n  \"ownerId\": \"user_123\",\n  \"visibility\": \"private\"\n}";

    public ImmutableArray<AccessRule> AccessRules { get; private set; } =
    [
        new("rule_01JCAB2D4F6H8J1M4P7S1V4ZB", "Owner read", "principal.id == document.ownerId", "local-draft", "local"),
        new("rule_01JCAB3E5G7J9K2N5Q8T2W5YC", "Public visibility", "document.visibility in [\"public\", \"unlisted\"]", "local-draft", "local"),
        new("rule_01JCAB4F6H8K1M3P6R9V3X6ZD", "Editor write", "principal.role in [\"owner\", \"admin\"] && has(document.status)", "local-draft", "local"),
        new("rule_01JCAB5G7I9L2N4Q7S1W4Y5AE", "Audit deny draft", "principal.id == document.ownerId &&", "local-draft", "local"),
    ];

    public AccessRuleValidationState? LastAccessRuleValidation { get; set; }
    public AccessRuleDryRunState? LastAccessRuleDryRun { get; set; }

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
