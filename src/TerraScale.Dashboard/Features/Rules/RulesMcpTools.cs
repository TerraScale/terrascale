using System.ComponentModel;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using TerraScale.Dashboard.Platform.Mcp;
using TerraScale.TerraBase.Contracts;
using TerraScale.TerraBase.Contracts.Management;

namespace TerraScale.Dashboard.Features.Rules
{
    [McpServerToolType]
    public sealed class RulesMcpTools(IHttpContextAccessor http, RulesBackend rules)
    {
        internal static readonly IReadOnlySet<string> Names = new HashSet<string>(StringComparer.Ordinal)
        {
            "get_database_rules", "deploy_database_rules", "test_database_rules", "list_database_rules_versions", "restore_database_rules_version",
        };

        internal static readonly IReadOnlySet<string> Writes = new HashSet<string>(StringComparer.Ordinal) { "deploy_database_rules", "restore_database_rules_version" };

        [McpServerTool(Name = "get_database_rules", Title = "Get database rules", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Read a database's current rules JSON {\"tables\":{\"<table>\":{\"read\",\"create\",\"update\",\"delete\",\"columns\",\"writable\",\"validate\",\"set\"}}}, its version, and the problems today's schema causes. version and rules are null when none are deployed, which denies every end-user request. Pass version to deploy_database_rules. Requires databases:read.")]
        public async Task<McpRules> GetRules(string databaseId, string? organizationId = null, string? projectId = null)
        {
            var target = await TargetAsync(databaseId, organizationId, projectId, false);
            return Output(await RulesBackend.CurrentAsync(target.Client, target.ProjectId, target.Database));
        }

        [McpServerTool(Name = "deploy_database_rules", Title = "Deploy database rules", ReadOnly = false, Destructive = true, OpenWorld = false, Idempotent = false, UseStructuredContent = true), Description("Replace a database's whole rules set with rules, a JSON object {\"tables\":{...}} as get_database_rules returns it. Pass the version get_database_rules returned, or null when none is deployed; a stale version is refused, so read again and redeploy. Every condition is checked against the current schema: returns deployed true with the new version and warnings, or deployed false with every problem by table and field. Test drafts with test_database_rules first. Requires databases:write.")]
        public async Task<McpRulesDeploy> DeployRules(string databaseId, JsonElement rules, string? version = null, string? organizationId = null, string? projectId = null)
        {
            var target = await TargetAsync(databaseId, organizationId, projectId, true);
            return await DeployAsync(() => RulesBackend.DeployAsync(target.Client, target.ProjectId, target.Database, version, rules));
        }

        [McpServerTool(Name = "test_database_rules", Title = "Test database rules", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("Simulate one end-user request against the deployed rules, or against a draft rules object, without reading or writing rows. operation is read, create, update or delete; auth is {\"uid\",\"claims\",\"org_id\",\"org_role\",\"is_anonymous\"}; row is the stored row and new the create body or update patch. Returns allowed, decidedBy, the deciding clause, every evaluated condition, the readable columns and the draft's problems. Requires databases:read.")]
        public async Task<TerraBaseRulesTestResult> TestRules(string databaseId, string operation, string table, JsonElement auth, JsonElement? row = null, JsonElement? @new = null,
            JsonElement? rules = null, string? organizationId = null, string? projectId = null)
        {
            var body = RulesBackend.TestBody(rules, operation, table, auth, row, @new);
            var target = await TargetAsync(databaseId, organizationId, projectId, false);
            return await RulesBackend.TestAsync(target.Client, target.ProjectId, target.Database, body);
        }

        [McpServerTool(Name = "list_database_rules_versions", Title = "List database rules versions", ReadOnly = true, Destructive = false, OpenWorld = false, Idempotent = true, UseStructuredContent = true), Description("List a database's kept rules versions, newest first, with when and by whom each was deployed. The newest is the current version. Pass an older version to restore_database_rules_version. Requires databases:read.")]
        public async Task<McpRulesVersions> ListRulesVersions(string databaseId, string? organizationId = null, string? projectId = null)
        {
            var target = await TargetAsync(databaseId, organizationId, projectId, false);
            return new(await RulesBackend.VersionsAsync(target.Client, target.ProjectId, target.Database));
        }

        [McpServerTool(Name = "restore_database_rules_version", Title = "Restore database rules version", ReadOnly = false, Destructive = true, OpenWorld = false, Idempotent = false, UseStructuredContent = true), Description("Deploy an earlier rules version again as a new version. Pass currentVersion from get_database_rules; a stale one is refused. The old rules are checked against today's schema like any deploy: returns deployed true with the new version, or deployed false with every problem. Requires databases:write.")]
        public async Task<McpRulesDeploy> RestoreRulesVersion(string databaseId, string version, string currentVersion, string? organizationId = null, string? projectId = null)
        {
            var target = await TargetAsync(databaseId, organizationId, projectId, true);
            return await DeployAsync(() => RulesBackend.RestoreAsync(target.Client, target.ProjectId, target.Database, version, currentVersion));
        }

        internal static async Task<CallToolResult> CallAsync(IManagementService client, string projectId, string databaseId, string tool, IDictionary<string, JsonElement>? arguments)
        {
            JsonElement? Value(string name) => arguments?.TryGetValue(name, out var value) == true && value.ValueKind != JsonValueKind.Null ? value : null;
            string? Text(string name) => Value(name) is { ValueKind: JsonValueKind.String } value ? value.GetString() : null;
            string Required(string name) => Text(name) ?? throw new AccountAccessException(name + " is required.", 422);
            return tool switch
            {
                "get_database_rules" => Reply(Output(await RulesBackend.CurrentAsync(client, projectId, databaseId)), RulesMcpJson.Default.McpRules),
                "deploy_database_rules" => Reply(await DeployAsync(() => RulesBackend.DeployAsync(client, projectId, databaseId, Text("version"),
                    Value("rules") ?? throw new AccountAccessException("rules is required.", 422))), RulesMcpJson.Default.McpRulesDeploy),
                "test_database_rules" => Reply(await RulesBackend.TestAsync(client, projectId, databaseId, RulesBackend.TestBody(Value("rules"), Required("operation"), Required("table"),
                    Value("auth") ?? throw new AccountAccessException("auth is required.", 422), Value("row"), Value("new"))), RulesMcpJson.Default.TerraBaseRulesTestResult),
                "list_database_rules_versions" => Reply(new McpRulesVersions(await RulesBackend.VersionsAsync(client, projectId, databaseId)), RulesMcpJson.Default.McpRulesVersions),
                "restore_database_rules_version" => Reply(await DeployAsync(() => RulesBackend.RestoreAsync(client, projectId, databaseId, Required("version"), Required("currentVersion"))),
                    RulesMcpJson.Default.McpRulesDeploy),
                _ => throw new InvalidOperationException("Unknown rules tool."),
            };
        }

        private static async Task<McpRulesDeploy> DeployAsync(Func<Task<TerraBaseRulesDeploy>> deploy)
        {
            try
            {
                var answer = await deploy();
                return new(answer.Rules is not null, answer.Rules?.Version, answer.Problems, answer.Rules?.Warnings ?? []);
            }
            catch (TerraBasePreconditionFailedException error) when (error.ErrorCode == "version_mismatch")
            {
                throw new AccountAccessException("The rules changed since that version. Call get_database_rules and deploy again.", 412);
            }
        }

        private static McpRules Output(TerraBaseRules? rules)
        {
            if (rules is null)
            {
                return new(null, null, [], null, null);
            }

            using var document = JsonDocument.Parse(RulesBackend.Source(rules.Tables));
            return new(rules.Version, document.RootElement.Clone(), rules.Problems ?? [], rules.UpdatedAt, rules.UpdatedBy);
        }

        private static CallToolResult Reply<T>(T value, JsonTypeInfo<T> type) => new()
        {
            StructuredContent = JsonSerializer.SerializeToElement(value, type),
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, type) }],
        };

        private async Task<(IManagementService Client, string ProjectId, string Database)> TargetAsync(string databaseId, string? organizationId, string? projectId, bool write)
        {
            var context = DashboardMcp.Scope(http.HttpContext, organizationId, projectId);
            return await rules.ClientAsync(context, DatabaseService.Database(context, databaseId), write, http.HttpContext?.RequestAborted ?? default);
        }
    }

    public sealed record McpRules([property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] JsonElement? Rules,
        IReadOnlyList<TerraBaseRulesProblem> Problems, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] DateTimeOffset? UpdatedAt,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? UpdatedBy);

    public sealed record McpRulesDeploy(bool Deployed, [property: JsonIgnore(Condition = JsonIgnoreCondition.Never)] string? Version, IReadOnlyList<TerraBaseRulesProblem> Problems,
        IReadOnlyList<TerraBaseRulesProblem> Warnings);

    public sealed record McpRulesVersions(IReadOnlyList<TerraBaseRulesVersion> Versions);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(McpRules))]
    [JsonSerializable(typeof(McpRulesDeploy))]
    [JsonSerializable(typeof(McpRulesVersions))]
    [JsonSerializable(typeof(TerraBaseRulesTestResult))]
    internal sealed partial class RulesMcpJson : JsonSerializerContext;
}
