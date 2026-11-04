using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.WebUtilities;
using ModelContextProtocol.Protocol;
using TerraScale.Dashboard.Features.Backups;
using TerraScale.Dashboard.Features.Data;
using TerraScale.Dashboard.Features.Databases;
using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.Dashboard.Platform.TsAuth;
using TerraScale.TerraBase.Contracts.Management;

namespace TerraScale.Dashboard.Platform.Mcp
{
    // Machine credentials never become dashboard users. Every operation retains the original
    // ts-auth project/database scopes and TerraBase authorizes the RPC with that same token.
    public sealed class McpApiKeyTools(IHttpClientFactory http, IDatabaseBackend databases, IBackupBackend backups, TerraBaseRpcClient rpc)
    {
        internal const string ContextKey = "TerraScale.Dashboard/mcp-api-key";
        internal static readonly HashSet<string> Tools = new(StringComparer.Ordinal)
        {
            "get_context", "list_databases", "get_database", "create_database", "branch_database", "delete_database",
            "execute_sql", "run_sql", "check_sql", "get_database_schema", "create_scoped_key",
            "create_backup", "restore_backup"
        };

        internal async Task<Credential?> AuthenticateAsync(string key, string? project, CancellationToken ct)
        {
            if (key.Length != 79 || key[26] != '_' || !TerraScaleIds.IsValid(key[..26]) || key.Any(char.IsWhiteSpace)) return null;
            try
            {
                var client = new TsAuthClient(http.CreateClient("platform"));
                var result = await client.Oidc.ExchangeApiKeyAsync(key, project, ct);
                // Claims come only from the fixed HTTPS ts-auth token endpoint, never a caller-supplied JWT.
                // TerraBase independently validates the signature, audience, expiry and live key revocation.
                var pieces = result.AccessToken.Split('.');
                if (result.TokenType != "Bearer" || result.ExpiresIn <= 0 || pieces.Length != 3) return null;
                using var body = JsonDocument.Parse(WebEncoders.Base64UrlDecode(pieces[1]));
                var claims = body.RootElement;
                var organizationId = claims.GetProperty("org_id").GetString()!;
                var projectId = claims.GetProperty("project_id").GetString()!;
                if (!TerraScaleIds.IsValid(organizationId) || !TerraScaleIds.IsValid(projectId) || project is { Length: > 0 } && project != projectId) return null;
                var databaseId = claims.TryGetProperty("database", out var narrowed) ? narrowed.GetString() : null;
                if (databaseId is not null && !TerraScaleIds.IsValid(databaseId)) return null;
                return new(key, new("", result.AccessToken, organizationId, "", projectId, "") { IsApiKey = true },
                    databaseId, (claims.GetProperty("scope").GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }
            catch (TsAuthException error) when (error.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden) { return null; }
            catch (Exception error) when (error is JsonException or FormatException or KeyNotFoundException) { return null; }
        }

        internal async Task<CallToolResult> InvokeAsync(Credential credential, string tool, IDictionary<string, JsonElement>? arguments, CancellationToken ct)
        {
            string Text(string name, string fallback = "") => arguments?.TryGetValue(name, out var value) == true && value.ValueKind != JsonValueKind.Null ? value.GetString() ?? fallback : fallback;
            long? Number(string name) => arguments?.TryGetValue(name, out var value) == true && value.ValueKind != JsonValueKind.Null
                ? value.ValueKind == JsonValueKind.String ? long.Parse(value.GetString()!, System.Globalization.CultureInfo.InvariantCulture) : value.GetInt64() : null;
            var scope = credential.Scope;
            if (!Tools.Contains(tool)) throw new AccountAccessException("This tool requires a dashboard sign-in. API keys reach database tools only.");
            if (Text("organizationId", scope.OrganizationId) != scope.OrganizationId || Text("projectId", scope.ProjectId) != scope.ProjectId)
                throw new AccountAccessException("Project is unavailable.", 404);
            var databaseId = Text("databaseId");
            if (credential.DatabaseId is { } only && databaseId.Length > 0 && databaseId != only)
                throw new AccountAccessException("Database was not found in this project.", 404);
            if (tool == "get_context") return Reply(new McpKeyContext(scope.OrganizationId, scope.ProjectId, credential.DatabaseId, credential.Scopes), McpAgentJson.Default.McpKeyContext);
            if (tool == "create_scoped_key")
            {
                var access = Text("access", "read_write");
                var ttl = Number("ttlSeconds") ?? 3600;
                if (!TerraScaleIds.IsValid(databaseId) || access is not ("read" or "read_write") || ttl is < 60 or > 2_592_000)
                    throw new AccountAccessException("Pass a database ULID, access read or read_write, and ttlSeconds between 60 and 2,592,000.", 422);
                var client = new TsAuthClient(http.CreateClient("platform"));
                try
                {
                    return Reply(await client.CreateScopedKeyAsync(credential.Key, new(scope.ProjectId, databaseId, access, (int)ttl), ct), McpAgentJson.Default.TsAuthScopedKey);
                }
                catch (TsAuthException error)
                {
                    throw new AccountAccessException("Scoped key minting was refused. The parent key must be active, unscoped and grant the requested database and access.", (int)(error.StatusCode ?? HttpStatusCode.BadGateway));
                }
            }
            var write = tool is "create_database" or "branch_database" or "delete_database" or "create_backup" or "restore_backup";
            var dataTool = tool is "execute_sql" or "run_sql" or "check_sql" or "get_database_schema";
            var sql = Text("sql").Trim();
            if (tool is "execute_sql" or "run_sql" or "check_sql")
            {
                if (SqlValidation.Diagnostics(sql).FirstOrDefault(item => item.Severity == "error") is { } problem)
                    throw new AccountAccessException(problem.Message, 422);
                write = !SqlValidation.IsRead(sql);
                if (write && tool == "run_sql") throw new AccountAccessException("run_sql accepts read-only statements. Use execute_sql for writes.", 422);
            }
            var permission = dataTool ? write ? "documents:write" : "documents:read" : write ? "databases:write" : "databases:read";
            if (!credential.Scopes.Contains(permission, StringComparer.Ordinal)) throw new AccountAccessException("The API key does not grant " + permission + ".");
            if (tool == "check_sql") return Reply(SqlValidation.Analyze(sql), McpAgentJson.Default.SqlAnalysis);
            var regions = await databases.RegionsAsync(ct);
            var region = DatabaseRegion.Resolve(regions, Text("region", Environment.GetEnvironmentVariable("TS_REGION") ?? regions[0].Id))
                ?? throw new AccountAccessException("Choose a region listed by TerraScale discovery.", 422);
            var management = rpc.User<IManagementService>(scope.AccessToken, region.Id).WithCancellationToken(ct);
            if (tool == "create_database")
            {
                var ttl = Number("ttlSeconds"); DatabaseService.ValidateTtl(ttl);
                return Reply(Output(await management.CreateDatabaseAsync(scope.ProjectId, new(Name: Text("name"), HomeRegion: region.Id, TtlSeconds: ttl))), McpAgentJson.Default.McpDatabase);
            }
            if (tool == "list_databases")
            {
                var limit = Number("limit") ?? 100;
                if (limit is < 1 or > 100) throw new AccountAccessException("limit must be between 1 and 100.", 422);
                var page = await management.ListDatabasesAsync(scope.ProjectId, Text("after") is { Length: > 0 } after ? after : null, (int)limit);
                return Reply(new McpDatabaseList([.. page.Items.Select(Output)], page.Next, page.Next is not null, (int)limit), McpAgentJson.Default.McpDatabaseList);
            }
            if (!TerraScaleIds.IsValid(databaseId)) throw new AccountAccessException(DatabaseService.DatabaseIdRule, 422);
            // Scoped data keys have no catalog scope. SQL routes accept ULIDs, so no privileged listing is needed.
            if (dataTool)
            {
                async Task<CallToolResult> ExecuteAsync(DatabaseRegion home)
                {
                    if (tool == "get_database_schema")
                    {
                        var catalog = await databases.SqlTablesAsync(scope, home, databaseId, ct);
                        return Reply(new SqlCatalog([], [.. catalog.Tables.Select(table => new SqlCatalogTable(table.Name,
                            [.. table.Columns.Select(column => new SqlCatalogField(column.Name, column.Decltype ?? "No declared type"))]))], catalog.Truncated), McpAgentJson.Default.SqlCatalog);
                    }
                    var result = await databases.SqlAsync(scope, home, databaseId, sql, write, ct);
                    return tool == "run_sql" ? Reply(new McpSqlResponse(new(result.Columns, result.Rows)), McpAgentJson.Default.McpSqlResponse)
                        : Reply(result, McpAgentJson.Default.DatabaseSqlResult);
                }
                try { return await ExecuteAsync(region); }
                catch (DatabaseWrongRegionException wrong) when (DatabaseRegion.Resolve(regions, wrong.HomeRegion) is { } home && home.Id != region.Id)
                {
                    // A 421 definitively refused execution. Never retry an ambiguous write failure.
                    return await ExecuteAsync(home);
                }
            }
            var database = await management.GetDatabaseAsync(scope.ProjectId, databaseId);
            region = DatabaseRegion.Resolve(await databases.RegionsAsync(ct), database.HomeRegion.Length > 0 ? database.HomeRegion : database.Region)
                ?? throw new AccountAccessException("The database home region is unavailable.", 503);
            management = rpc.User<IManagementService>(scope.AccessToken, region.Id).WithCancellationToken(ct);
            if (tool == "get_database") return Reply(Output(database), McpAgentJson.Default.McpDatabase);
            if (tool == "branch_database")
            {
                var ttl = Number("ttlSeconds"); DatabaseService.ValidateTtl(ttl);
                return Reply(Output(await management.BranchWithTtlAsync(scope.ProjectId, databaseId, Text("name"), ttl)), McpAgentJson.Default.McpDatabase);
            }
            if (tool == "delete_database")
            {
                if (McpScope.Alias(Text("confirmName"), Text("confirmation"), "confirmName") != database.Name) throw new AccountAccessException("Type the database name to confirm deletion.", 422);
                await management.DeleteDatabaseAsync(scope.ProjectId, databaseId);
                return Reply(new McpDeleted(true), McpAgentJson.Default.McpDeleted);
            }
            if (tool == "create_backup")
            {
                var backup = await backups.CreateAsync(scope, region, databaseId, ct);
                return Reply(new BackupView(backup.Id, backup.Status, backup.CreatedAt, backup.SizeBytes), McpAgentJson.Default.BackupView);
            }
            var backupId = Text("backupId");
            if (backupId.Length == 0)
            {
                backupId = (await backups.ListAsync(scope, region, databaseId, ct)).OrderByDescending(backup => backup.CreatedAt).FirstOrDefault()?.Id
                    ?? throw new AccountAccessException("That restore point is no longer available.", 404);
            }
            var restored = await backups.RestoreAsync(scope, region, databaseId, backupId, Text("name") is { Length: > 0 } name ? name : null, ct);
            return Reply(new BackupRestoreResult(restored.Id, restored.Name, "ready"), McpAgentJson.Default.BackupRestoreResult);
        }

        private static McpDatabase Output(TerraBaseManagedDatabase db) => new(db.Id, db.Name, "ready", db.HomeRegion.Length > 0 ? db.HomeRegion : db.Region,
            [.. (db.ReplicaRegions ?? []).Prepend(db.HomeRegion)], db.CreatedAt, db.AllRegions, db.ExpiresAt);
        private static CallToolResult Reply<T>(T value, JsonTypeInfo<T> type) => new()
        {
            StructuredContent = JsonSerializer.SerializeToNode(value, type) as JsonObject,
            Content = [new TextContentBlock { Text = JsonSerializer.Serialize(value, type) }]
        };

        internal sealed record Credential(string Key, DatabaseScope Scope, string? DatabaseId, string[] Scopes)
        {
            public override string ToString() => "MCP API key [REDACTED]";
        }
    }

    internal sealed record McpKeyContext(string OrganizationId, string ProjectId, string? DatabaseId, IReadOnlyList<string> Scopes);
    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(McpKeyContext))]
    [JsonSerializable(typeof(McpDatabase))]
    [JsonSerializable(typeof(McpDatabaseList))]
    [JsonSerializable(typeof(McpDeleted))]
    [JsonSerializable(typeof(SqlAnalysis))]
    [JsonSerializable(typeof(SqlCatalog))]
    [JsonSerializable(typeof(DatabaseSqlResult))]
    [JsonSerializable(typeof(McpSqlResponse))]
    [JsonSerializable(typeof(TsAuthScopedKey))]
    [JsonSerializable(typeof(BackupRestoreResult))]
    [JsonSerializable(typeof(BackupView))]
    [JsonSerializable(typeof(JsonElement))]
    internal sealed partial class McpAgentJson : JsonSerializerContext;
}
