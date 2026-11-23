using System.Buffers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using TerraScale.Dashboard.Errors;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.Dashboard.Platform.Clients;

namespace TerraScale.Dashboard.Features.Rules
{
    public sealed record RulesRequest(string Operation, string Table, string Auth, string Row, string New);

    public readonly union RulesVersionResult(TerraBaseRules, DashboardNotFound);

    public readonly union RulesDeployResult(TerraBaseRulesDeploy, DashboardValidation);

    public readonly union RulesTestOutcome(TerraBaseRulesTestResult, DashboardValidation);

    public readonly union RulesSourceInput(JsonElement, DashboardValidation);

    public sealed class RulesBackend(DatabaseService databases, IProjectAccessTokens tokens, TerraBaseHttpClient terraBase)
    {
        public const int MaxVersions = 100;

        public const string EmptySource = "{\n  \"tables\": {}\n}";

        public static readonly IReadOnlyList<string> Operations = ["read", "create", "update", "delete"];

        private static readonly string[] ReadScopes = ["databases:read"];

        private static readonly string[] WriteScopes = ["databases:read", "databases:write"];

        private const int MaxSourceBytes = 256 * 1024;

        public async Task<TerraBaseRules?> CurrentAsync(AccountContext context, DatabaseRecord database, CancellationToken cancellationToken)
        {
            var target = await ClientAsync(context, database, false, cancellationToken);
            return await CurrentAsync(target.Client, target.ProjectId, target.Database);
        }

        public async Task<RulesVersionResult> VersionAsync(AccountContext context, DatabaseRecord database, string version, CancellationToken cancellationToken)
        {
            if (!TerraScaleIds.IsValid(version))
            {
                return new DashboardNotFound("This rules version does not exist.");
            }

            var target = await ClientAsync(context, database, false, cancellationToken);
            var rules = Valid(await target.Client.GetRulesVersionAsync(target.ProjectId, target.Database, version));
            return rules.Version == version ? rules : throw new JsonException("Rules returned another version.");
        }

        public async Task<IReadOnlyList<TerraBaseRulesVersion>> VersionsAsync(AccountContext context, DatabaseRecord database, CancellationToken cancellationToken)
        {
            var target = await ClientAsync(context, database, false, cancellationToken);
            return await VersionsAsync(target.Client, target.ProjectId, target.Database);
        }

        public Task<RulesDeployResult> DeployAsync(AccountContext context, DatabaseRecord database, string? version, string source, CancellationToken cancellationToken) =>
            Parse(source) switch
            {
                JsonElement rules => DeployValidatedAsync(context, database, version, rules, cancellationToken),
                DashboardValidation invalid => Task.FromResult<RulesDeployResult>(invalid),
            };

        private async Task<RulesDeployResult> DeployValidatedAsync(AccountContext context, DatabaseRecord database, string? version, JsonElement rules, CancellationToken cancellationToken)
        {
            var target = await ClientAsync(context, database, true, cancellationToken);
            return await DeployAsync(target.Client, target.ProjectId, target.Database, version, rules);
        }

        public async Task<TerraBaseRulesDeploy> RestoreAsync(AccountContext context, DatabaseRecord database, string version, string current, CancellationToken cancellationToken)
        {
            var target = await ClientAsync(context, database, true, cancellationToken);
            return await RestoreAsync(target.Client, target.ProjectId, target.Database, version, current);
        }

        public async Task<RulesTestOutcome> TestAsync(AccountContext context, DatabaseRecord database, string? draft, RulesRequest request, CancellationToken cancellationToken)
        {
            if (draft is null)
            {
                return await TestBodyAsync(context, database, null, request, cancellationToken);
            }

            var parsed = Parse(draft);
            return parsed switch
            {
                JsonElement rules => await TestBodyAsync(context, database, rules, request, cancellationToken),
                DashboardValidation error => error,
            };
        }

        private async Task<RulesTestOutcome> TestBodyAsync(AccountContext context, DatabaseRecord database, JsonElement? rules, RulesRequest request,
            CancellationToken cancellationToken)
        {
            var body = TestBody(rules, request);
            var target = await ClientAsync(context, database, false, cancellationToken);
            return await TestAsync(target.Client, target.ProjectId, target.Database, body);
        }

        internal static async Task<TerraBaseRules?> CurrentAsync(ManagementClient client, string projectId, string database)
        {
            try
            {
                return Valid(await client.GetRulesAsync(projectId, database));
            }
            catch (TerraBaseNotFoundException error) when (error.ErrorCode == "no_rules")
            {
                return null;
            }
        }

        internal static async Task<IReadOnlyList<TerraBaseRulesVersion>> VersionsAsync(ManagementClient client, string projectId, string database)
        {
            var page = await client.ListRulesVersionsAsync(projectId, database, null, MaxVersions);
            return page.Items is { Count: <= MaxVersions } items && items.All(item => item is not null && TerraScaleIds.IsValid(item.Version) && item.UpdatedBy is not null)
                ? items
                : throw new JsonException("Rules returned invalid versions.");
        }

        internal static async Task<TerraBaseRulesDeploy> DeployAsync(ManagementClient client, string projectId, string database, string? version, JsonElement rules)
        {
            if (version is not null && !TerraScaleIds.IsValid(version) || rules.ValueKind != JsonValueKind.Object)
            {
                throw new AccountAccessException("Send the rules as a JSON object and the version they replace.", 400);
            }

            return Valid(await client.DeployRulesAsync(projectId, database, version, rules));
        }

        internal static async Task<TerraBaseRulesDeploy> RestoreAsync(ManagementClient client, string projectId, string database, string version, string current)
        {
            if (!TerraScaleIds.IsValid(version) || !TerraScaleIds.IsValid(current))
            {
                throw new AccountAccessException("This rules version does not exist.", 404);
            }

            return Valid(await client.RestoreRulesVersionAsync(projectId, database, version, current));
        }

        internal static async Task<TerraBaseRulesTestResult> TestAsync(ManagementClient client, string projectId, string database, JsonElement body)
        {
            var result = await client.TestRulesAsync(projectId, database, body);
            return result is { DecidedBy: not null, Results: not null, Columns: not null, Problems: not null }
                ? result
                : throw new JsonException("Rules returned an invalid simulation.");
        }

        public static string Source(JsonElement tables)
        {
            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 256 }))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("tables");
                tables.WriteTo(writer);
                writer.WriteEndObject();
            }

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }

        internal static RulesSourceInput Parse(string source)
        {
            if (Encoding.UTF8.GetByteCount(source) > MaxSourceBytes)
            {
                return new DashboardValidation("Rules are at most 256 KiB.");
            }

            if (Json(source, "Rules must be a JSON object.") is not { ValueKind: JsonValueKind.Object } rules)
            {
                return new DashboardValidation("Rules must be a JSON object.");
            }

            return rules;
        }

        private static JsonElement TestBody(JsonElement? rules, RulesRequest request) =>
            TestBody(rules, request.Operation, request.Table, Json(request.Auth, "Auth must be a JSON object."),
                Values(request.Row, "Row must be a JSON object."), Values(request.New, "New must be a JSON object."));

        internal static JsonElement TestBody(JsonElement? rules, string operation, string table, JsonElement auth, JsonElement? row, JsonElement? values)
        {
            if (!Operations.Contains(operation))
            {
                throw new AccountAccessException("Choose read, create, update or delete.", 400);
            }

            if (table.Trim().Length is 0 or > 128)
            {
                throw new AccountAccessException("Choose a table.", 400);
            }

            if (auth.ValueKind != JsonValueKind.Object)
            {
                throw new AccountAccessException("Auth must be a JSON object.", 400);
            }

            if (row is { ValueKind: not JsonValueKind.Object } || values is { ValueKind: not JsonValueKind.Object })
            {
                throw new AccountAccessException("Row and new must be JSON objects.", 400);
            }

            if (rules is { ValueKind: not JsonValueKind.Object })
            {
                throw new AccountAccessException("Rules must be a JSON object.", 400);
            }

            var buffer = new ArrayBufferWriter<byte>();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { MaxDepth = 256 }))
            {
                writer.WriteStartObject();
                writer.WriteString("operation", operation);
                writer.WriteString("table", table.Trim());
                Write(writer, "auth", auth);
                Write(writer, "row", row);
                Write(writer, "new", values);
                Write(writer, "rules", rules);
                writer.WriteEndObject();
            }

            using var document = JsonDocument.Parse(buffer.WrittenMemory, new JsonDocumentOptions { MaxDepth = 256 });
            return document.RootElement.Clone();
        }

        private static void Write(Utf8JsonWriter writer, string name, JsonElement? value)
        {
            if (value is { } present)
            {
                writer.WritePropertyName(name);
                present.WriteTo(writer);
            }
        }

        private static JsonElement? Values(string text, string message)
        {
            if (text.Trim().Length == 0)
            {
                return null;
            }

            var values = Json(text, message);
            return values.ValueKind == JsonValueKind.Object ? values : throw new AccountAccessException(message, 400);
        }

        private static JsonElement Json(string text, string message)
        {
            try
            {
                using var document = JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = 256 });
                return document.RootElement.Clone();
            }
            catch (JsonException)
            {
                throw new AccountAccessException(message, 400);
            }
        }

        private static TerraBaseRules Valid(TerraBaseRules rules) =>
            rules is { Version: not null, UpdatedBy: not null } && TerraScaleIds.IsValid(rules.Version) && rules.Tables.ValueKind == JsonValueKind.Object
                ? rules
                : throw new JsonException("Rules returned an invalid response.");

        private static TerraBaseRulesDeploy Valid(TerraBaseRulesDeploy deploy) =>
            deploy switch
            {
                { Rules: { } rules, Problems.Count: 0 } => deploy with { Rules = Valid(rules) },
                { Rules: null, Problems.Count: > 0 } => deploy,
                _ => throw new JsonException("Rules returned an invalid deploy."),
            };

        internal async Task<(ManagementClient Client, string ProjectId, string Database)> ClientAsync(AccountContext context, DatabaseRecord database, bool write, CancellationToken cancellationToken)
        {
            context = await databases.CurrentAsync(context, cancellationToken);
            database = DatabaseService.Database(context, database.Id);
            if (write && !context.CanWriteProjectData)
            {
                throw new AccountAccessException("Write access is required to deploy rules.");
            }

            var scope = DatabaseService.Scope(context);
            if (PlatformDatabases.IsPlatform(scope.OrganizationId))
            {
                throw PlatformDatabases.ReadOnly();
            }

            var home = await databases.HomeAsync(context, database, cancellationToken);
            var access = await tokens.GetAsync(context.Session, scope.OrganizationId, scope.ProjectId, write ? WriteScopes : ReadScopes, cancellationToken);
            return (new ManagementClient(terraBase, ServiceCaller.User(access), cancellationToken), scope.ProjectId, database.Name);
        }
    }
}
