using TerraScale.Dashboard.Platform.Clients;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Features.Auth;

namespace TerraScale.Dashboard.Common.Databases
{
    public sealed record DatabaseScope(string UserId, string AccessToken, string OrganizationId, string OrganizationName, string ProjectId, string ProjectName)
    {
        public SessionRecord? Session { get; init; }
        internal bool IsApiKey { get; init; }
        public override string ToString()
        {
            return $"Database scope {OrganizationId}/{ProjectId} [REDACTED]";
        }
    }

    public sealed record DatabaseRegion(string Code, string Id, string Name, string Country, Uri Api, bool Healthy)
    {
        public static DatabaseRegion? FromId(string? id)
        {
            return id is { Length: >= 1 and <= 63 } && id[0] is >= 'a' and <= 'z' && id[^1] != '-' &&
            id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
                ? new(id.ToUpperInvariant(), id, DatabaseFormat.Region(id), "", new($"https://{id}.api.terrascale.tech/"), true)
                : null;
        }

        public static DatabaseRegion? Resolve(IReadOnlyList<DatabaseRegion> catalog, string? code)
        {
            return catalog.FirstOrDefault(region => region.Code == code || region.Id == code) ?? FromId(code?.ToLowerInvariant());
        }
    }
    public sealed record ManagedDatabase(string Id, string ProjectId, string Name, DateTimeOffset CreatedAt, string? BranchedFrom = null, string HomeRegion = "", IReadOnlyList<string>? ReplicaRegions = null, bool AllRegions = false)
    {
        public IReadOnlyList<string>? Regions { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public string? PreviousName { get; init; }
        public string? ConflictOf { get; init; }
    }
    public sealed record DatabaseSqlRow(string Id, JsonElement Value);
    public sealed record DatabaseSqlCatalog(IReadOnlyList<DatabaseSqlTable> Tables, bool Truncated);
    public sealed record DatabaseSqlTable(string Name, IReadOnlyList<DatabaseSqlColumn> Columns, IReadOnlyList<DatabaseTableColumn>? ColumnDetails = null)
    {
        public IReadOnlyList<DatabaseTableColumn> Schema => ColumnDetails ?? [.. Columns.Select(column => new DatabaseTableColumn(column.Name, column.Decltype))];
    }
    public sealed record DatabaseSqlColumn(string Name, string? Decltype);
    public sealed record DatabaseTableColumn(string Name, string? Decltype, bool NotNull = false, string? DefaultValue = null, int PrimaryKey = 0);
    public sealed record DatabaseSqlResult(IReadOnlyList<DatabaseSqlColumn> Columns, IReadOnlyList<IReadOnlyList<JsonElement>> Rows, long RowsAffected, long? LastInsertRowId);
    public sealed record DatabaseBackendFailure(int Status, string Message);
    public readonly union DatabaseRegionsOutcome(IReadOnlyList<DatabaseRegion>, DatabaseBackendFailure);
    public readonly union DatabaseSqlCatalogOutcome(DatabaseSqlCatalog, DatabaseBackendFailure);
    public readonly union DatabaseSqlBatchOutcome(IReadOnlyList<DatabaseSqlResult>, DatabaseBackendFailure);
    public sealed record ProjectCredential(string Id, string ProjectId, string Name, IReadOnlyList<string> Scopes, string? DatabaseId, string LastFour, DateTimeOffset CreatedAt)
    {
        public string CreatedByUserId { get; init; } = "";
        public string CreatedByEmail { get; init; } = "";
        public string? DatabaseName { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        public DateTimeOffset? LastUsedAt { get; init; }
        public bool IsActive { get; init; } = true;
        public IReadOnlyList<string>? AllowedCidrs { get; init; }
        public DateTimeOffset? PreviousSecretExpiresAt { get; init; }
    }
    public sealed record CreatedProjectCredential(ProjectCredential Key, string Secret)
    {
        public override string ToString()
        {
            return $"Project credential {Key.Id} [REDACTED]";
        }
    }

    public interface IDatabaseBackend
    {
        Task<DatabaseDiscovery> DiscoveryAsync(CancellationToken cancellationToken);
        Task<IReadOnlyList<DatabaseRegion>> RegionsAsync(CancellationToken cancellationToken);
        Task<IReadOnlyList<DatabaseRegion>> CatalogAsync(CancellationToken cancellationToken);
        Task<IReadOnlyList<ManagedDatabase>> ListAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken);
        Task<ManagedDatabase> CreateAsync(DatabaseScope scope, string name, DatabaseRegion home, IReadOnlyList<string> replicaRegions, bool allRegions, CancellationToken cancellationToken, long? ttlSeconds = null);
        Task<ManagedDatabase> PlaceAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<string> replicaRegions, bool allRegions, CancellationToken cancellationToken);
        Task<ManagedDatabase> GetAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<ManagedDatabase> PlaceRegionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<string> regions, bool allRegions, CancellationToken cancellationToken);
        Task<ManagedDatabase> MoveHomeAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string homeRegion, CancellationToken cancellationToken);
        Task<ManagedDatabase> BranchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken, long? ttlSeconds = null);
        Task<ManagedDatabase> RenameAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken);
        Task<ManagedDatabase> SetTtlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, long? ttlSeconds, CancellationToken cancellationToken);
        Task DeleteAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<DatabaseSqlCatalog> SqlTablesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<DatabaseSqlResult> SqlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string statement, bool write, CancellationToken cancellationToken);
        Task<DatabaseRegionsOutcome> TryRegionsAsync(CancellationToken cancellationToken);
        Task<DatabaseSqlCatalogOutcome> TrySqlTablesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<DatabaseSqlBatchOutcome> TrySqlBatchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<string> statements, bool write, CancellationToken cancellationToken);
    }

    public sealed class DatabaseBackend(TerraBaseHttpClient terraBase, IProjectAccessTokens tokens) : IDatabaseBackend
    {
        public async Task<IReadOnlyList<DatabaseRegion>> RegionsAsync(CancellationToken cancellationToken)
        {
            return (await DiscoveryAsync(cancellationToken)).Regions;
        }

        public Task<DatabaseDiscovery> DiscoveryAsync(CancellationToken cancellationToken)
        {
            return ServerTiming.ReadAsync((this, "region-discovery"), () => ReadDiscoveryAsync(cancellationToken));
        }

        private async Task<DatabaseDiscovery> ReadDiscoveryAsync(CancellationToken cancellationToken)
        {
            try
            {
                var catalog = await CatalogAsync(cancellationToken);
                return new(catalog, catalog);
            }
            catch (Exception error) when (error is TerraBaseException or TimeoutException or HttpRequestException or JsonException)
            {
                return new(await AnsweringRegionAsync(cancellationToken), []);
            }
        }

        public Task<IReadOnlyList<DatabaseRegion>> CatalogAsync(CancellationToken cancellationToken)
        {
            return ServerTiming.ReadAsync((this, "region-catalog"), () => ReadCatalogAsync(cancellationToken));
        }

        private async Task<IReadOnlyList<DatabaseRegion>> ReadCatalogAsync(CancellationToken cancellationToken)
        {
            var catalog = (await terraBase.ListRegionsAsync(cancellationToken)).Require();
            var regions = catalog.Regions.Select(DatabaseRegion.FromId).ToArray();
            if (regions.Length == 0 || regions.Any(region => region is null) || regions.DistinctBy(region => region!.Id).Count() != regions.Length)
            {
                throw new HttpRequestException("TerraBase returned an invalid region catalog.");
            }

            return [.. regions.OfType<DatabaseRegion>().OrderBy(region => TerraScale.Dashboard.Common.UI.Regions.Rank(region.Id)).ThenBy(region => region.Id, StringComparer.Ordinal)];
        }

        private async Task<IReadOnlyList<DatabaseRegion>> AnsweringRegionAsync(CancellationToken cancellationToken)
        {
            var outcome = await terraBase.HealthAsync(cancellationToken);
            var health = outcome switch
            {
                TerraBaseHealth answered => answered,
                TerraBaseRefusal refusal => throw new HttpRequestException("TerraBase did not name a verified serving region.", refusal.Exception()),
            };
            if (DatabaseRegion.FromId(health.Region) is not { } serving)
            {
                throw new HttpRequestException("TerraBase did not name a valid serving region.");
            }

            return [serving];
        }

        public Task<IReadOnlyList<ManagedDatabase>> ListAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken)
        {
            return ServerTiming.ReadAsync((this, "databases", scope.UserId, scope.AccessToken, scope.OrganizationId, scope.ProjectId, home.Api, home.Id),
                () => ReadDatabasesAsync(scope, home, cancellationToken));
        }

        private async Task<IReadOnlyList<ManagedDatabase>> ReadDatabasesAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken)
        {
            var caller = await ClientAsync(scope, "databases:read", cancellationToken);
            var result = new List<ManagedDatabase>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? next = null;
            var pages = 0;
            do
            {
                TerraBaseDatabasePage page;
                var outcome = await terraBase.ListDatabasesAsync(caller, scope.ProjectId, next, 100, cancellationToken);
                page = outcome switch
                {
                    TerraBaseDatabasePage listed => listed,
                    TerraBaseRefusal { Code: "invalid_response" } => throw new DatabaseListingException("Database listing returned an invalid response."),
                    TerraBaseRefusal refusal => throw refusal.Exception(),
                };
                foreach (var item in page.Items)
                {
                    if (item.ProjectId != scope.ProjectId || !TerraScaleIds.IsValid(item.Id) ||
                        result.Any(existing => existing.Id == item.Id || existing.Name == item.Name))
                    {
                        throw new DatabaseListingException("Database listing returned an invalid identity.");
                    }

                    result.Add(Map(item));
                }
                next = page.Next;
                if (!string.IsNullOrEmpty(next) && (!seen.Add(next) || ++pages >= 100))
                {
                    throw new DatabaseListingException("Database pagination did not advance within its bounds.");
                }
            } while (!string.IsNullOrEmpty(next));
            return result;
        }

        public async Task<ManagedDatabase> CreateAsync(DatabaseScope scope, string name, DatabaseRegion home, IReadOnlyList<string> replicaRegions, bool allRegions, CancellationToken cancellationToken, long? ttlSeconds = null)
        {
            var caller = await ClientAsync(scope, "databases:write", cancellationToken);
            return Map(await RunAsync(async () => (await terraBase.CreateDatabaseAsync(caller, scope.ProjectId, new(Name: name, HomeRegion: home.Id,
                ReplicaRegions: allRegions || replicaRegions.Count == 0 ? null : replicaRegions, AllRegions: allRegions ? true : null, TtlSeconds: ttlSeconds), cancellationToken)).Require()));
        }

        public async Task<ManagedDatabase> GetAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var caller = await ClientAsync(scope, "databases:read", cancellationToken);
            return Map(await RunAsync(async () => (await terraBase.GetDatabaseAsync(caller, scope.ProjectId, databaseId, cancellationToken)).Require()));
        }

        public async Task<ManagedDatabase> PlaceRegionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<string> regions, bool allRegions, CancellationToken cancellationToken)
        {
            var caller = await ClientAsync(scope, "databases:write", cancellationToken);
            return Map(await RunAsync(async () => (await terraBase.SetDatabaseAsync(caller, scope.ProjectId, databaseId,
                allRegions ? new(AllRegions: true) : new(Regions: regions), cancellationToken)).Require()));
        }

        public async Task<ManagedDatabase> PlaceAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<string> replicaRegions, bool allRegions, CancellationToken cancellationToken)
        {
            var caller = await ClientAsync(scope, "databases:write", cancellationToken);
            return Map(await RunAsync(async () => (await terraBase.SetDatabaseAsync(caller, scope.ProjectId, databaseId,
                allRegions ? new(AllRegions: true) : new(HomeRegion: home.Id, ReplicaRegions: replicaRegions), cancellationToken)).Require()));
        }

        public async Task<ManagedDatabase> MoveHomeAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string homeRegion, CancellationToken cancellationToken)
        {
            var current = await GetAsync(scope, home, databaseId, cancellationToken);
            if (current.AllRegions || (current.Regions ?? []).Contains(homeRegion, StringComparer.Ordinal))
            {
                return current;
            }

            return await PlaceRegionsAsync(scope, home, databaseId, [.. current.Regions ?? [], homeRegion], false, cancellationToken);
        }

        public async Task<ManagedDatabase> BranchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken, long? ttlSeconds = null)
        {
            var source = await NameAsync(scope, home, databaseId, cancellationToken);
            var caller = await ClientAsync(scope, "databases:write", cancellationToken);
            return Map(await RunAsync(async () => (await terraBase.BranchAsync(caller, scope.ProjectId, source, name, ttlSeconds, cancellationToken)).Require()));
        }

        public async Task<ManagedDatabase> SetTtlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, long? ttlSeconds, CancellationToken cancellationToken)
        {
            var name = await NameAsync(scope, home, databaseId, cancellationToken);
            var caller = await ClientAsync(scope, "databases:write", cancellationToken);
            return Map(await RunAsync(async () => (await (ttlSeconds is { } seconds
                ? terraBase.SetDatabaseTtlAsync(caller, scope.ProjectId, name, seconds, cancellationToken)
                : terraBase.ClearDatabaseTtlAsync(caller, scope.ProjectId, name, cancellationToken))).Require()));
        }

        public async Task DeleteAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            if ((await ListAsync(scope, home, cancellationToken)).SingleOrDefault(item => item.Id == databaseId) is not { } database)
            {
                return;
            }

            var caller = await ClientAsync(scope, "databases:write", cancellationToken);
            await CommittedStepAsync(async () => (await terraBase.DeleteDatabaseAsync(caller, scope.ProjectId, database.Name, cancellationToken)).Require(), cancellationToken);
        }

        private static async Task CommittedStepAsync(Func<Task> step, CancellationToken cancellationToken)
        {
            for (var attempt = 1; ; attempt++)
            {
                try { await RunAsync(step); return; }
                catch (TerraBaseNotFoundException) { return; }
                catch (TerraBaseUnavailableException error) when (error.ErrorCode == "catalog_anchor_unavailable" && attempt < 3)
                { await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken); }
            }
        }

        public async Task<ManagedDatabase> RenameAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken)
        {
            var caller = await ClientAsync(scope, "databases:write", cancellationToken);
            return Map(await RunAsync(async () => (await terraBase.SetDatabaseAsync(caller, scope.ProjectId, databaseId, new(Name: name), cancellationToken)).Require()));
        }

        public async Task<DatabaseSqlResult> SqlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string statement, bool write, CancellationToken cancellationToken)
        {
            var result = (await SqlBatchAsync(scope, home, databaseId, [new(statement)], write, cancellationToken))[0];
            return new(result.Columns, result.Rows, result.RowsAffected, result.LastInsertRowid);
        }

        public async Task<DatabaseRegionsOutcome> TryRegionsAsync(CancellationToken cancellationToken)
        {
            try
            {
                return await RegionsAsync(cancellationToken);
            }
            catch (Exception error) when (ExternalFailure(error, cancellationToken))
            {
                return new DatabaseBackendFailure(DatabaseResults.Status(error), DatabaseResults.Message(error));
            }
        }

        public async Task<DatabaseSqlCatalogOutcome> TrySqlTablesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            try
            {
                return await SqlTablesAsync(scope, home, databaseId, cancellationToken);
            }
            catch (Exception error) when (ExternalFailure(error, cancellationToken))
            {
                return new DatabaseBackendFailure(DatabaseResults.Status(error), DatabaseResults.Message(error));
            }
        }

        public async Task<DatabaseSqlBatchOutcome> TrySqlBatchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId,
            IReadOnlyList<string> statements, bool write, CancellationToken cancellationToken)
        {
            try
            {
                var results = await SqlBatchAsync(scope, home, databaseId, [.. statements.Select(statement => new SqlExecuteStatement(statement))], write, cancellationToken);
                return (IReadOnlyList<DatabaseSqlResult>)[.. results.Select(result => new DatabaseSqlResult(result.Columns, result.Rows, result.RowsAffected, result.LastInsertRowid))];
            }
            catch (Exception error) when (ExternalFailure(error, cancellationToken))
            {
                return new DatabaseBackendFailure(DatabaseResults.Status(error), DatabaseResults.Message(error));
            }
        }

        private static bool ExternalFailure(Exception error, CancellationToken cancellationToken)
        {
            return error is HttpRequestException or TerraBaseException or DatabaseProviderException or TerraScale.Dashboard.Platform.TsAuth.TsAuthException
                || error is OperationCanceledException && !cancellationToken.IsCancellationRequested;
        }

        private async Task<IReadOnlyList<SqlExecuteResult>> SqlBatchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId,
            IReadOnlyList<SqlExecuteStatement> statements, bool write, CancellationToken cancellationToken)
        {
            if (PlatformDatabases.IsPlatformProject(scope.ProjectId))
            {
                throw PlatformDatabases.ReadOnly();
            }

            var name = await NameAsync(scope, home, databaseId, cancellationToken);
            var token = scope.IsApiKey ? scope.AccessToken : await tokens.GetAsync(
                scope.Session ?? throw new HttpRequestException("Current user authorization is required."),
                scope.OrganizationId, scope.ProjectId, write ? ["sql:read", "sql:write"] : ["sql:read"], cancellationToken);
            if (!write)
            {
                return await ExecuteSqlAsync(home, scope.ProjectId, name, token, statements, cancellationToken);
            }

            var attemptId = AccountService.NewId();
            try { return await ExecuteSqlAsync(home, scope.ProjectId, name, token, statements, cancellationToken); }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && (error is TimeoutException or IOException or OperationCanceledException ||
                error is TerraBaseException { Status: >= 500 } || error is HttpRequestException { StatusCode: null } || error is HttpRequestException { StatusCode: { } status } && (int)status >= 500))
            {
                throw new SqlOutcomeUnknownException(attemptId);
            }
        }

        private async Task<IReadOnlyList<SqlExecuteResult>> ExecuteSqlAsync(DatabaseRegion home, string projectId, string name, string token,
            IReadOnlyList<SqlExecuteStatement> statements, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var request = JsonSerializer.SerializeToElement(new SqlExecuteRequest(statements), DatabaseSqlJson.Default.SqlExecuteRequest);
            var outcome = await terraBase.ExecuteSqlAsync(ServiceCaller.User(token), projectId, name, request, timeout.Token);
            var body = outcome switch
            {
                JsonElement answer => answer.Deserialize(DatabaseSqlJson.Default.SqlExecuteResponse),
                TerraBaseRefusal { Status: 400 or 403 or 409 or 413 } refusal => throw new DatabaseSqlException((HttpStatusCode)refusal.Status,
                    refusal.Code == "forbidden_scope" ? "Write access is required to change data or schema." : refusal.Message, refusal.Code, refusal.SqlState),
                TerraBaseRefusal refusal => throw refusal.Exception(),
            };
            if (body?.Results is not { } results || results.Count != statements.Count || results.Any(result => result is null || result.Columns is null || result.Rows is null ||
                result.Columns.Any(column => column is null) || result.Rows.Any(row => row is null || row.Count != result.Columns.Count)))
            {
                throw new HttpRequestException("TerraBase returned an invalid SQL result.");
            }

            return results;
        }

        public async Task<DatabaseSqlCatalog> SqlTablesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            var listed = (await SqlBatchAsync(scope, home, databaseId,
                [new("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name LIMIT 10000")], false, timeout.Token))[0];
            if (listed.Rows.Count > 10000)
            {
                throw new HttpRequestException("TerraBase returned an invalid SQL catalog.");
            }

            var names = listed.Rows.Select(row => CatalogText(listed, row, "name")).Where(name => !TerraBaseSqlNames.IsInternalTable(name)).Take(101).ToArray();
            if (names.Any(string.IsNullOrWhiteSpace) || names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Length)
            {
                throw new HttpRequestException("TerraBase returned an invalid SQL catalog.");
            }

            if (names.Length == 0)
            {
                return new([], false);
            }

            var columns = await SqlBatchAsync(scope, home, databaseId,
                [.. names.Take(100).Select(name => new SqlExecuteStatement("PRAGMA table_info('" + name.Replace("'", "''", StringComparison.Ordinal) + "')"))], false, timeout.Token);
            var tables = columns.Select((result, index) =>
            {
                DatabaseTableColumn[] details = [.. result.Rows.Select(row => new DatabaseTableColumn(CatalogText(result, row, "name"), CatalogText(result, row, "type"),
                    CatalogValue(result, row, "notnull") is { ValueKind: JsonValueKind.Number } notNull && notNull.TryGetInt64(out var required) && required > 0,
                    CatalogValue(result, row, "dflt_value") is { ValueKind: JsonValueKind.String } fallback ? fallback.GetString() : null,
                    CatalogValue(result, row, "pk") is { ValueKind: JsonValueKind.Number } key && key.TryGetInt32(out var position) && position > 0 ? position : 0))];
                return new DatabaseSqlTable(names[index], [.. details.Select(column => new DatabaseSqlColumn(column.Name, column.Decltype))], details);
            }).ToArray();
            if (tables.Any(table => table.Columns.Count == 0 || table.Columns.Any(column => string.IsNullOrWhiteSpace(column.Name)) ||
                table.Columns.Select(column => column.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != table.Columns.Count))
            {
                throw new HttpRequestException("TerraBase returned incomplete SQL metadata. Refresh the catalog.");
            }

            return new(tables, names.Length > 100);
        }

        private static string CatalogText(SqlExecuteResult result, IReadOnlyList<JsonElement> row, string field)
        {
            var index = result.Columns.ToList().FindIndex(column => column.Name == field);
            return index >= 0 && index < row.Count && row[index].ValueKind == JsonValueKind.String ? row[index].GetString()!
                : throw new HttpRequestException("TerraBase returned invalid SQL metadata.");
        }

        private static JsonElement CatalogValue(SqlExecuteResult result, IReadOnlyList<JsonElement> row, string field)
        {
            var index = result.Columns.ToList().FindIndex(column => column.Name == field);
            return index >= 0 && index < row.Count ? row[index] : default;
        }

        private async Task<ServiceCaller> ClientAsync(DatabaseScope scope, string permission, CancellationToken cancellationToken)
        {
            if (PlatformDatabases.IsPlatformProject(scope.ProjectId))
            {
                throw PlatformDatabases.ReadOnly();
            }

            if (scope.IsApiKey)
            {
                return ServiceCaller.User(scope.AccessToken);
            }

            var session = scope.Session ?? throw new HttpRequestException("Current user authorization is required.");
            var token = await tokens.GetAsync(session, scope.OrganizationId, scope.ProjectId, [permission], cancellationToken);
            return ServiceCaller.User(token);
        }

        private async Task<string> NameAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            if (scope.IsApiKey) return databaseId;
            return (await ListAsync(scope, home, cancellationToken)).SingleOrDefault(item => item.Id == databaseId)?.Name
                ?? throw new DatabaseProviderException(HttpStatusCode.NotFound);
        }

        private static ManagedDatabase Map(TerraBaseManagedDatabase item)
        {
            return new(item.Id, item.ProjectId, item.Name, item.CreatedAt,
            item.BranchedFrom, item.HomeRegion, item.Regions is { } regions ? [.. regions.Where(region => region != item.HomeRegion)] : item.ReplicaRegions, item.AllRegions)
            { Regions = item.Regions, PreviousName = item.PreviousName, ConflictOf = item.ConflictOf, ExpiresAt = item.ExpiresAt };
        }

        private static async Task<T> RunAsync<T>(Func<Task<T>> operation)
        {
            try { return await operation(); }
            catch (TerraBaseWrongRegionException error) { throw new DatabaseWrongRegionException(error.Region, error.HomeRegion, error.Regions); }
        }
        private static async Task RunAsync(Func<Task> operation)
        {
            try { await operation(); }
            catch (TerraBaseWrongRegionException error) { throw new DatabaseWrongRegionException(error.Region, error.HomeRegion, error.Regions); }
        }

    }

    public sealed class DatabaseSqlException(HttpStatusCode status, string message, string? code = null, string? sqlstate = null)
        : DatabaseProviderException(status, message + (code is { Length: > 0 and <= 64 } && code != "forbidden_scope" ? " (" + code + ")" : ""))
    {
        public string? Code { get; } = code is { Length: > 0 and <= 64 } ? code : null;

        public string? Sqlstate { get; } = sqlstate is { Length: 5 } ? sqlstate : null;
    }

    public sealed class SqlOutcomeUnknownException(string attemptId) : DatabaseProviderException(HttpStatusCode.ServiceUnavailable,
        $"SQL outcome unknown: the statement may have run. Check the data before running it again. Attempt {attemptId}.")
    {
        public string AttemptId { get; } = attemptId;
    }

    internal sealed record SqlExecuteRequest(IReadOnlyList<SqlExecuteStatement> Statements);
    internal sealed record SqlExecuteStatement(string Sql);
    internal sealed record SqlExecuteResponse(IReadOnlyList<SqlExecuteResult>? Results, SqlExecuteError? Error);
    internal sealed record SqlExecuteResult(IReadOnlyList<DatabaseSqlColumn> Columns, IReadOnlyList<IReadOnlyList<JsonElement>> Rows,
        [property: JsonPropertyName("rows_affected")] long RowsAffected, [property: JsonPropertyName("last_insert_rowid")] long? LastInsertRowid);
    internal sealed record SqlExecuteError(string Code, string Message, string? Sqlstate = null);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(SqlExecuteRequest))]
    [JsonSerializable(typeof(SqlExecuteResponse))]
    internal sealed partial class DatabaseSqlJson : JsonSerializerContext;

    public class DatabaseProviderException(HttpStatusCode status, string message = "The database service could not complete the request.") : HttpRequestException(message, null, status)
    {
        public TimeSpan? RetryAfter { get; init; }
    }
    internal sealed class DatabaseListingException(string message) : DatabaseProviderException(HttpStatusCode.ServiceUnavailable, message);

    public sealed class DatabaseWrongRegionException(string? region, string? homeRegion, IReadOnlyList<string> regions)
        : DatabaseProviderException(HttpStatusCode.MisdirectedRequest, homeRegion is null
            ? "The database is served in another region. Nothing was changed."
            : $"The database is managed in {DatabaseFormat.Region(homeRegion)}. Nothing was changed.")
    {
        public string? Region { get; } = region;
        public string? HomeRegion { get; } = homeRegion;
        public IReadOnlyList<string> Regions { get; } = regions;

    }
    public static class RegionNames
    {
        public static string List(IReadOnlyList<string> codes)
        {
            string[] names = [.. codes.Select(code => UI.Regions.Name(code) is { Length: > 0 } name ? name : code.ToLowerInvariant())];
            return names.Length < 2 ? string.Join("", names) : string.Join(", ", names[..^1]) + " and " + names[^1];
        }
    }
}
