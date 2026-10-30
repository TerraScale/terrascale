using TerraScale.Dashboard.Platform.Rpc;
using TerraScale.TerraBase.Contracts.Management;
using TerraScale.TerraBase.Contracts.Regions;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.TerraBase.Contracts;

namespace TerraScale.Dashboard.Common.Databases
{
    public sealed record DatabaseScope(string UserId, string AccessToken, string OrganizationId, string OrganizationName, string ProjectId, string ProjectName)
    {
        public SessionRecord? Session { get; init; }
        public override string ToString()
        {
            return $"Database scope {OrganizationId}/{ProjectId} [REDACTED]";
        }
    }

    public sealed record DatabaseRegion(string Code, string Id, string Name, string Country, Uri Api, bool Healthy)
    {
        // TerraBase's regional contract (terrascale docs/terrabase/region-identity.md): a region id is a lowercase letter,
        // then lowercase letters, digits or hyphens, at most 63 characters, not ending in a hyphen, and
        // https://<id>.api.terrascale.tech/ serves only that region. Anything else derives no origin.
        public static DatabaseRegion? FromId(string? id)
        {
            return id is { Length: >= 1 and <= 63 } && id[0] is >= 'a' and <= 'z' && id[^1] != '-' &&
            id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '-')
                ? new(id.ToUpperInvariant(), id, DatabaseFormat.Region(id), "", new($"https://{id}.api.terrascale.tech/"), true)
                : null;
        }

        // A stored or producer-named region: the catalog's entry, else the region its id names. A home or replica outside
        // the catalog keeps its own region instead of being dropped or moved.
        public static DatabaseRegion? Resolve(IReadOnlyList<DatabaseRegion> catalog, string? code)
        {
            return catalog.FirstOrDefault(region => region.Code == code || region.Id == code) ?? FromId(code?.ToLowerInvariant());
        }
    }
    public sealed record ManagedDatabase(string Id, string ProjectId, string Name, DateTimeOffset CreatedAt, string? BranchedFrom = null, string HomeRegion = "", IReadOnlyList<string>? ReplicaRegions = null, bool AllRegions = false)
    {
        public string? PreviousName { get; init; }
        public string? ConflictOf { get; init; }
    }
    public sealed record CollectionStats(string Name, long Documents, long Bytes);
    public sealed record DatabaseSqlRow(string Id, JsonElement Value);
    public sealed record DatabaseSqlCatalog(IReadOnlyList<DatabaseSqlTable> Tables, bool Truncated);
    public sealed record DatabaseSqlTable(string Name, IReadOnlyList<DatabaseSqlColumn> Columns);
    public sealed record DatabaseSqlColumn(string Name, string? Decltype);
    // One relational statement's result from TerraBase POST .../sql/execute: columns, rows (one value per column), and write counts.
    public sealed record DatabaseSqlResult(IReadOnlyList<DatabaseSqlColumn> Columns, IReadOnlyList<IReadOnlyList<JsonElement>> Rows, long RowsAffected, long? LastInsertRowId);
    public sealed record DatabaseIndex(string Name, string Collection, IReadOnlyList<string> Fields, string State);
    public sealed record ProjectCredential(string Id, string ProjectId, string Name, IReadOnlyList<string> Scopes, string? DatabaseId, string LastFour, DateTimeOffset CreatedAt)
    {
        public string CreatedByUserId { get; init; } = "";
        public string CreatedByEmail { get; init; } = "";
        public string? DatabaseName { get; init; }
        public DateTimeOffset? ExpiresAt { get; init; }
        // Last successful token exchange as ts-auth reports it (at most five minutes stale); null when never used.
        public DateTimeOffset? LastUsedAt { get; init; }
        public bool IsActive { get; init; } = true;
        // ts-auth's canonical CIDRs a request with this key must come from; empty allows any IP, null when not reported.
        public IReadOnlyList<string>? AllowedCidrs { get; init; }
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
        Task<IReadOnlyList<DatabaseRegion>> RegionsAsync(CancellationToken cancellationToken);
        Task<IReadOnlyList<DatabaseRegion>> CatalogAsync(CancellationToken cancellationToken);
        Task<IReadOnlyList<ManagedDatabase>> ListAsync(DatabaseScope scope, DatabaseRegion home, CancellationToken cancellationToken);
        Task<ManagedDatabase> CreateAsync(DatabaseScope scope, string name, IReadOnlyList<DatabaseRegion> placement, bool allRegions, CancellationToken cancellationToken);
        Task<ManagedDatabase> BranchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken);
        Task<ManagedDatabase> RenameAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken);
        Task DeleteAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<ManagedDatabase> SetRegionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<DatabaseRegion> placement, bool allRegions, CancellationToken cancellationToken);
        Task<IReadOnlyList<CollectionStats>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<DatabaseSqlCatalog> SqlTablesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<DatabaseSqlResult> SqlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string statement, bool write, CancellationToken cancellationToken);
        Task<IReadOnlyList<DatabaseIndex>> IndexesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken);
        Task<DatabaseIndex> CreateIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string collection, IReadOnlyList<string> fields, CancellationToken cancellationToken);
        Task DropIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken);

    }

    // Canonical data operations use TerraBase RPC with current action-scoped authority.
    public sealed class DatabaseBackend(TerraBaseRpcClient rpc, IProjectAccessTokens tokens) : IDatabaseBackend
    {
        // TerraBase's anonymous GET /v1/regions answers {"regions":[...]}: the id of every region it serves now, from any region,
        // so a new region appears without a change here. gru, fra and sjc keep their display order; others follow by id. While
        // that route is not deployed, or answers anything but a valid list, the one verified region is the healthz region.
        public Task<IReadOnlyList<DatabaseRegion>> RegionsAsync(CancellationToken cancellationToken)
        {
            return ServerTiming.ReadAsync((this, "regions"), () => ReadRegionsAsync(cancellationToken));
        }

        private async Task<IReadOnlyList<DatabaseRegion>> ReadRegionsAsync(CancellationToken cancellationToken)
        {
            try { return await CatalogAsync(cancellationToken); }
            catch (Exception error) when (error is TerraBaseException or HttpRequestException or JsonException) { return await AnsweringRegionAsync(cancellationToken); }
        }

        // Absence requires a complete catalog: a health-only fallback cannot discover an unknown home.
        public Task<IReadOnlyList<DatabaseRegion>> CatalogAsync(CancellationToken cancellationToken)
        {
            return ServerTiming.ReadAsync((this, "region-catalog"), () => ReadCatalogAsync(cancellationToken));
        }

        private async Task<IReadOnlyList<DatabaseRegion>> ReadCatalogAsync(CancellationToken cancellationToken)
        {
            var catalog = await rpc.Platform<IRegionsService>().WithCancellationToken(cancellationToken).ListAsync();
            var regions = catalog.Regions.Select(DatabaseRegion.FromId).ToArray();
            if (regions.Length == 0 || regions.Any(region => region is null) || regions.DistinctBy(region => region!.Id).Count() != regions.Length)
            {
                throw new HttpRequestException("TerraBase returned an invalid region catalog.");
            }

            return [.. regions.OfType<DatabaseRegion>().OrderBy(region => TerraScale.Dashboard.Common.UI.Regions.Rank(region.Id)).ThenBy(region => region.Id, StringComparer.Ordinal)];
        }

        // The region answering api.terrascale.tech, named by both its /healthz body and its X-TerraBase-Region header. It is not
        // necessarily the nearest, a database's home, or every serving region.
        private async Task<IReadOnlyList<DatabaseRegion>> AnsweringRegionAsync(CancellationToken cancellationToken)
        {
            var health = await rpc.Platform<IRegionsService>().WithCancellationToken(cancellationToken).HealthAsync();
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
            var client = await ClientAsync(scope, home, "databases:read", cancellationToken);
            var result = new List<ManagedDatabase>();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            string? next = null;
            var pages = 0;
            do
            {
                var page = await RunAsync(() => client.ListDatabasesAsync(scope.ProjectId, next, 100).ResponseAsync);
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

        public async Task<ManagedDatabase> CreateAsync(DatabaseScope scope, string name, IReadOnlyList<DatabaseRegion> placement, bool allRegions, CancellationToken cancellationToken)
        {
            var client = await ClientAsync(scope, placement[0], "databases:write", cancellationToken);
            // TerraBase refuses allRegions together with any replicaRegions list, even an empty one, and refuses an empty list on
            // a cell (decision 0013a: two regions or more). With only the home chosen, it places the database in every other serving region.
            return Map(await RunAsync(() => client.CreateDatabaseAsync(scope.ProjectId, new(Name: name, HomeRegion: placement[0].Id, ReplicaRegions:
                allRegions || placement.Count == 1 ? null : placement.Skip(1).Select(region => region.Id).ToArray(), allRegions ? true : null)).ResponseAsync));
        }

        public async Task<ManagedDatabase> BranchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken)
        {
            var source = await NameAsync(scope, home, databaseId, cancellationToken);
            var client = await ClientAsync(scope, home, "databases:write", cancellationToken);
            return Map(await RunAsync(() => client.BranchAsync(scope.ProjectId, source, name).ResponseAsync));
        }

        public async Task DeleteAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            // A database its home region no longer lists is already deleted, for example by an earlier attempt TerraBase
            // committed but answered 503: the caller then only drops its record.
            if ((await ListAsync(scope, home, cancellationToken)).SingleOrDefault(item => item.Id == databaseId) is not { } database)
            {
                return;
            }

            var project = await ClientAsync(scope, home, "databases:write", cancellationToken);
            await CommittedStepAsync(() => project.DeleteDatabaseAsync(scope.ProjectId, database.Name).ResponseAsync, cancellationToken);
        }

        // TerraBase answers 503 catalog_anchor_unavailable when it committed a catalog change but could not write its restore
        // anchor in time, and asks for the same request again; deletion is idempotent, so it is sent at most three
        // times. A 404 (unknown_database, or unknown_route under project access) means the database is already gone.
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
            var project = await ClientAsync(scope, home, "databases:write", cancellationToken);
            return Map(await RunAsync(() => project.SetDatabaseAsync(scope.ProjectId, databaseId, new(Name: name)).ResponseAsync));
        }

        public async Task<ManagedDatabase> SetRegionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, IReadOnlyList<DatabaseRegion> placement, bool allRegions, CancellationToken cancellationToken)
        {
            var name = await NameAsync(scope, home, databaseId, cancellationToken);
            var project = await ClientAsync(scope, home, "databases:write", cancellationToken);
            return Map(await RunAsync(() => project.SetDatabaseAsync(scope.ProjectId, name,
                new(ReplicaRegions: allRegions ? null : [.. placement.Skip(1).Select(region => region.Id)], AllRegions: allRegions ? true : null)).ResponseAsync));
        }

        public async Task<IReadOnlyList<CollectionStats>> CollectionsAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var name = await NameAsync(scope, home, databaseId, cancellationToken);
            var database = await ClientAsync(scope, home, "databases:read", cancellationToken);
            return [.. (await RunAsync(() => database.ListCollectionStatsAsync(scope.ProjectId, name).ResponseAsync)).Items.Select(item => new CollectionStats(item.Name, item.Documents, item.Bytes))];
        }

        // SQL execution and catalog batches share current-user authority, home-region routing and JSON bounds.
        public async Task<DatabaseSqlResult> SqlAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string statement, bool write, CancellationToken cancellationToken)
        {
            var result = (await SqlBatchAsync(scope, home, databaseId, [new(statement)], write, cancellationToken))[0];
            return new(result.Columns, result.Rows, result.RowsAffected, result.LastInsertRowid);
        }

        private async Task<IReadOnlyList<SqlExecuteResult>> SqlBatchAsync(DatabaseScope scope, DatabaseRegion home, string databaseId,
            IReadOnlyList<SqlExecuteStatement> statements, bool write, CancellationToken cancellationToken)
        {
            if (PlatformDatabases.IsPlatform(scope.OrganizationId))
            {
                throw PlatformDatabases.ReadOnly();
            }

            var name = await NameAsync(scope, home, databaseId, cancellationToken);
            var session = scope.Session ?? throw new HttpRequestException("Current user authorization is required.");
            var token = await tokens.GetAsync(session, scope.OrganizationId, scope.ProjectId, write ? ["documents:read", "documents:write"] : ["documents:read"], cancellationToken);
            if (!write)
            {
                return await ExecuteSqlAsync(home, scope.ProjectId, name, token, statements, cancellationToken);
            }

            var attemptId = AccountService.NewId();
            try { return await ExecuteSqlAsync(home, scope.ProjectId, name, token, statements, cancellationToken); }
            catch (Exception error) when (!cancellationToken.IsCancellationRequested && (error is TimeoutException or IOException or OperationCanceledException ||
                error is TerraBaseException { Status: >= 500 } || error is HttpRequestException { StatusCode: null } || error is HttpRequestException { StatusCode: { } status } && (int)status >= 500))
            {
                // A lost, cut, timed-out or malformed answer may follow a commit, and /sql/execute has no idempotency key.
                throw new SqlOutcomeUnknownException(attemptId);
            }
        }

        private async Task<IReadOnlyList<SqlExecuteResult>> ExecuteSqlAsync(DatabaseRegion home, string projectId, string name, string token,
            IReadOnlyList<SqlExecuteStatement> statements, CancellationToken cancellationToken)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            SqlExecuteResponse? body;
            try
            {
                var answer = await rpc.User<IManagementService>(token, home.Id).WithCancellationToken(timeout.Token)
                    .ExecuteSqlAsync(projectId, name, JsonSerializer.SerializeToElement(new SqlExecuteRequest(statements), DatabaseSqlJson.Default.SqlExecuteRequest));
                body = answer.Deserialize(DatabaseSqlJson.Default.SqlExecuteResponse);
            }
            catch (TerraBaseWrongRegionException error) { throw new DatabaseWrongRegionException(error.Region, error.HomeRegion, error.Regions); }
            catch (TerraBaseException error) when (error.Status is 400 or 403 or 409 or 413)
            {
                throw new DatabaseSqlException((HttpStatusCode)error.Status, error.ErrorCode == "forbidden_scope" ? "Write access is required to change data or schema." : error.Message);
            }
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
                [new("SELECT name FROM sqlite_master WHERE type='table' ORDER BY name LIMIT 101")], false, timeout.Token))[0];
            if (listed.Rows.Count > 101)
            {
                throw new HttpRequestException("TerraBase returned an invalid SQL catalog.");
            }

            var names = listed.Rows.Select(row => CatalogText(listed, row, "name")).ToArray();
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
            var tables = columns.Select((result, index) => new DatabaseSqlTable(names[index], [.. result.Rows.Select(row =>
                new DatabaseSqlColumn(CatalogText(result, row, "name"), CatalogText(result, row, "type")))])).ToArray();
            // A dropped table answers no metadata; fail this snapshot instead of presenting stale tables as complete.
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
            return index >= 0 && row[index].ValueKind == JsonValueKind.String ? row[index].GetString()!
                : throw new HttpRequestException("TerraBase returned invalid SQL metadata.");
        }

        public async Task<IReadOnlyList<DatabaseIndex>> IndexesAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            var name = await NameAsync(scope, home, databaseId, cancellationToken);
            var database = await ClientAsync(scope, home, "databases:read", cancellationToken);
            return [.. (await RunAsync(() => database.ListIndexesAsync(scope.ProjectId, name).ResponseAsync)).Items.Select(Map)];
        }

        public async Task<DatabaseIndex> CreateIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string collection, IReadOnlyList<string> fields, CancellationToken cancellationToken)
        {
            var databaseName = await NameAsync(scope, home, databaseId, cancellationToken);
            var database = await ClientAsync(scope, home, "databases:write", cancellationToken);
            return Map(await RunAsync(() => database.CreateIndexAsync(scope.ProjectId, databaseName, new(collection, fields)).ResponseAsync));
        }

        public async Task DropIndexAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, string name, CancellationToken cancellationToken)
        {
            var databaseName = await NameAsync(scope, home, databaseId, cancellationToken);
            var database = await ClientAsync(scope, home, "databases:write", cancellationToken);
            await RunAsync(() => database.DropIndexAsync(scope.ProjectId, databaseName, name).ResponseAsync);
        }

        private async Task<IManagementService> ClientAsync(DatabaseScope scope, DatabaseRegion home, string permission, CancellationToken cancellationToken)
        {
            if (PlatformDatabases.IsPlatform(scope.OrganizationId))
            {
                throw PlatformDatabases.ReadOnly();
            }

            var session = scope.Session ?? throw new HttpRequestException("Current user authorization is required.");
            var token = await tokens.GetAsync(session, scope.OrganizationId, scope.ProjectId, [permission], cancellationToken);
            return rpc.User<IManagementService>(token, home.Id).WithCancellationToken(cancellationToken);
        }

        // UI URLs use database IDs; the authoritative database list resolves those IDs to canonical route names.
        private async Task<string> NameAsync(DatabaseScope scope, DatabaseRegion home, string databaseId, CancellationToken cancellationToken)
        {
            return (await ListAsync(scope, home, cancellationToken)).SingleOrDefault(item => item.Id == databaseId)?.Name
                ?? throw new DatabaseProviderException(HttpStatusCode.NotFound);
        }

        private static ManagedDatabase Map(TerraBaseManagedDatabase item)
        {
            return new(item.Id, item.ProjectId, item.Name, item.CreatedAt,
            item.BranchedFrom, item.HomeRegion, item.ReplicaRegions, item.AllRegions)
            { PreviousName = item.PreviousName, ConflictOf = item.ConflictOf };
        }

        private static DatabaseIndex Map(TerraBaseIndex item)
        {
            return new(item.Name, item.Collection, item.Fields, item.State);
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

    // TerraBase refused a SQL statement; its message names the rule that failed and never quotes the statement.
    public sealed class DatabaseSqlException(HttpStatusCode status, string message, string? sqliteCode = null)
        : DatabaseProviderException(status, message + (sqliteCode is { Length: > 0 and <= 64 } ? " (" + sqliteCode + ")" : ""))
    {
        public string? SqliteCode { get; } = sqliteCode is { Length: > 0 and <= 64 } ? sqliteCode : null;
    }

    // A SQL write whose answer was lost may have committed; the dashboard never resends it.
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
    internal sealed record SqlExecuteError(string Code, string Message, [property: JsonPropertyName("sqlite_code")] string? SqliteCode = null);

    [JsonSourceGenerationOptions(JsonSerializerDefaults.Web)]
    [JsonSerializable(typeof(SqlExecuteRequest))]
    [JsonSerializable(typeof(SqlExecuteResponse))]
    internal sealed partial class DatabaseSqlJson : JsonSerializerContext;

    public class DatabaseProviderException(HttpStatusCode status, string message = "The database service could not complete the request.") : HttpRequestException(message, null, status)
    {
        // The producer's Retry-After on a refusal, when it sent one.
        public TimeSpan? RetryAfter { get; init; }
    }
    // A successful producer's invalid listing cannot be treated as an unreachable region.
    internal sealed class DatabaseListingException(string message) : DatabaseProviderException(HttpStatusCode.ServiceUnavailable, message);

    // TerraBase's wrong-region refusal retains the answering region and the database's placement; calls are never redirected.
    public sealed class DatabaseWrongRegionException(string? region, string? homeRegion, IReadOnlyList<string> regions)
        : DatabaseProviderException(HttpStatusCode.MisdirectedRequest, homeRegion is null
            ? "The database is served in another region. Nothing was changed."
            : $"The database is managed in {DatabaseFormat.Region(homeRegion)}. Nothing was changed.")
    {
        public string? Region { get; } = region;
        public string? HomeRegion { get; } = homeRegion;
        public IReadOnlyList<string> Regions { get; } = regions;

    }
}
