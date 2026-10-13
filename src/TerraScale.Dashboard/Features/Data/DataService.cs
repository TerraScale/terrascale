using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.Dashboard.Features.Accounts;
using TerraScale.Dashboard.Features.Auth;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

public sealed partial class DataService(DatabaseService databases, IDocumentBackend documents, IDatabaseBackend backend, IProjectAccessTokens projectTokens)
{
    public async Task<IReadOnlyList<TerraBaseCollection>> CollectionsAsync(AccountContext context, string databaseId, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        return await documents.CollectionsAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Name, cancellationToken);
    }

    public async Task<IReadOnlyDictionary<string, long>> CollectionCountsAsync(AccountContext context, string databaseId, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        var reported = await backend.CollectionsAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), databaseId, cancellationToken);
        var result = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var collection in reported)
            if (collection.Documents < 0 || !result.TryAdd(ValidCollection(collection.Name), collection.Documents))
                throw new HttpRequestException("The data plane returned ambiguous collection counts.");
        return result;
    }

    public async Task<DocumentBrowse> BrowseAsync(AccountContext context, string databaseId, string collection, string search, string sort, bool descending, int page, CancellationToken cancellationToken)
    {
        collection = ValidCollection(collection);
        var database = DatabaseService.Database(context, databaseId);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        var path = sort.Length == 0 ? "$id" : "/" + sort.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        if (sort.Length > 128) throw new AccountAccessException("Sort field is too long.", 422);
        var rows = new List<TerraBaseDocument>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var query = new TerraBaseQuery { Sort = [new(path, descending ? "desc" : "asc")], Limit = 1000 };
        string? cursor = null;
        do
        {
            var result = await documents.DocumentsAsync(DatabaseService.Scope(context), home, database.Name, collection, query with { Limit = 1000 - rows.Count, Cursor = cursor }, cancellationToken);
            rows.AddRange(result.Items);
            cursor = result.NextCursor;
            if (!string.IsNullOrEmpty(cursor) && !seen.Add(cursor)) throw new HttpRequestException("Document pagination did not advance.");
        } while (!string.IsNullOrEmpty(cursor) && rows.Count < 1000);
        search = search.Trim();
        var matches = search.Length == 0 ? rows : rows.Where(row => (row.Id + " " + row.Document.GetRawText()).Contains(search, StringComparison.OrdinalIgnoreCase)).ToList();
        var pages = Math.Max(1, (matches.Count + 24) / 25);
        page = Math.Clamp(page, 1, pages);
        return new(matches.Skip((page - 1) * 25).Take(25).ToArray(), matches.Count, page, pages, !string.IsNullOrEmpty(cursor));
    }

    public async Task<IReadOnlyList<TerraBaseDocument>> DocumentsAsync(AccountContext context, string databaseId, string collection, CancellationToken cancellationToken)
        => await ListAsync(context, databaseId, collection, "", 100, "", cancellationToken);

    public async Task<IReadOnlyList<TerraBaseDocument>> ListAsync(AccountContext context, string databaseId, string collection, string search, int limit, string after, CancellationToken cancellationToken)
    {
        if (limit == 0) limit = 100;
        if (limit is < 1 or > 100) throw new AccountAccessException("Document limits use 1–100 rows.", 422);
        var database = DatabaseService.Database(context, databaseId);
        var result = await documents.ListAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Name,
            ValidCollection(collection), limit, after.Length == 0 ? null : after, cancellationToken);
        search = search.Trim();
        return result.Items.Where(row => search.Length == 0 || (row.Id + " " + row.Document.GetRawText()).Contains(search, StringComparison.OrdinalIgnoreCase)).Take(limit).ToArray();
    }

    public async Task CreateCollectionAsync(AccountContext context, string databaseId, string collection, CancellationToken cancellationToken)
    {
        if (!context.CanWrite) throw new AccountAccessException("Write access is required.");
        if (!CollectionPattern().IsMatch(collection)) throw new AccountAccessException("Collection names start with a lowercase letter and use up to 63 lowercase letters, digits, hyphens, and underscores.", 422);
        var database = DatabaseService.Database(context, databaseId);
        await documents.CreateCollectionAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Name, collection, cancellationToken);
    }

    public async Task<TerraBaseWriteResult> WriteAsync(AccountContext context, string databaseId, string collection, string operation, string id, string body, string version, CancellationToken cancellationToken)
    {
        if (!context.CanWrite) throw new AccountAccessException("Write access is required.");
        var database = DatabaseService.Database(context, databaseId);
        collection = ValidCollection(collection);
        if (operation is not ("create" or "replace" or "patch" or "delete") || operation != "create" && id.Length == 0)
            throw new AccountAccessException("Choose a document and an operation.", 422);
        if (id.Length > 256 || id.Any(char.IsControl)) throw new AccountAccessException("Document IDs use up to 256 characters without control characters.", 422);
        JsonElement document = default;
        if (operation != "delete")
        {
            if (body.Length == 0 || System.Text.Encoding.UTF8.GetByteCount(body) > 1024 * 1024) throw new AccountAccessException("Enter a valid JSON document of 1 MiB or less.", 422);
            try { using var parsed = JsonDocument.Parse(body); document = parsed.RootElement.Clone(); }
            catch (JsonException) { throw new AccountAccessException("Enter a valid JSON document of 1 MiB or less.", 422); }
            if (document.ValueKind != JsonValueKind.Object) throw new AccountAccessException(operation == "patch" ? "A patch must be a JSON object; its fields are merged into the document." : "A document must be a JSON object.", 422);
        }
        var scope = DatabaseService.Scope(context);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        if (operation != "create" && version.Length == 0)
        {
            var current = await documents.GetAsync(scope, home, database.Name, collection, id, cancellationToken)
                ?? throw new AccountAccessException("Document is unavailable.", 404);
            if (current.State != "value" || string.IsNullOrWhiteSpace(current.Version)) throw new IOException("The document has no unambiguous current version.");
            version = current.Version;
        }
        return await documents.WriteAsync(scope, home, database.Name, collection, operation, id, document, version, cancellationToken);
    }

    public async Task<TerraBaseDocument> GetAsync(AccountContext context, string databaseId, string collection, string id, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        collection = ValidCollection(collection);
        if (id.Length is 0 or > 256 || id.Any(char.IsControl)) throw new AccountAccessException("Choose a valid document ID.", 422);
        return await documents.GetAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Name, collection, id, cancellationToken)
            ?? throw new AccountAccessException("Document is unavailable.", 404);
    }

    public async Task<TerraBaseCollectionSchema> SchemaAsync(AccountContext context, string databaseId, string collection, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        collection = ValidCollection(collection);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        var scope = DatabaseService.Scope(context);
        var token = await projectTokens.GetAsync(context.Session, scope.OrganizationId, scope.ProjectId, cancellationToken);
        return await documents.SchemaAsync(scope with { AccessToken = token }, home, database.Name, collection, cancellationToken);
    }

    public async Task<DatabaseSqlResult> SqlAsync(AccountContext context, string databaseId, string statement, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        statement = statement.Trim();
        if (SqlValidation.Diagnostics(statement).FirstOrDefault(item => item.Severity == "error") is { } problem) throw new AccountAccessException(problem.Message, 422);
        return await backend.SqlAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Id, statement, cancellationToken);
    }

    public async Task<SqlCatalog> CatalogAsync(AccountContext context, string databaseId, IReadOnlyList<TerraBaseCollection> collections, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        var scope = DatabaseService.Scope(context);
        var entries = await Task.WhenAll(collections.Select(async (collection, index) =>
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal) { ["_id"] = "string", ["_rev"] = "string" };
            long count = 0;
            var approximate = true;
            if (index < 20)
            {
                try
                {
                    var sample = await documents.DocumentsAsync(scope, home, database.Name, collection.Name, new TerraBaseQuery { Limit = 20, Count = true }, cancellationToken);
                    count = sample.Count?.Value ?? sample.Items.Count;
                    approximate = sample.Count?.Exact != true;
                    foreach (var row in sample.Items) Infer(row.Document, "", 0, fields);
                }
                catch (HttpRequestException) { }
            }
            return new SqlCatalogCollection(collection.Name, count, approximate, "sample", fields.OrderBy(field => field.Key, StringComparer.Ordinal).Select(field => new SqlCatalogField(field.Key, field.Value)).ToArray());
        }));
        return new(["SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "ORDER BY", "ASC", "DESC", "LIMIT", "TRUE", "FALSE"], entries);
    }

    private static void Infer(JsonElement value, string prefix, int depth, Dictionary<string, string> fields)
    {
        if (value.ValueKind != JsonValueKind.Object || depth >= 8) return;
        foreach (var field in value.EnumerateObject())
        {
            var path = prefix + field.Name;
            var type = field.Value.ValueKind switch { JsonValueKind.String => "string", JsonValueKind.Number => "number", JsonValueKind.True or JsonValueKind.False => "boolean", JsonValueKind.Object => "object", JsonValueKind.Array => "array", _ => "null" };
            fields[path] = fields.TryGetValue(path, out var existing) && existing != type ? "mixed" : type;
            Infer(field.Value, path + ".", depth + 1, fields);
        }
    }

    public async Task<IReadOnlyList<DatabaseIndex>> IndexesAsync(AccountContext context, string databaseId, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        return await backend.IndexesAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Id, cancellationToken);
    }

    public async Task CreateIndexAsync(AccountContext context, string databaseId, string collection, string fields, CancellationToken cancellationToken)
        => await CreateIndexFieldsAsync(context, databaseId, collection, fields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal).ToArray(), cancellationToken);

    public async Task CreateIndexFieldAsync(AccountContext context, string databaseId, string collection, string field, CancellationToken cancellationToken)
        => await CreateIndexFieldsAsync(context, databaseId, collection, [field.Trim()], cancellationToken);

    private async Task CreateIndexFieldsAsync(AccountContext context, string databaseId, string collection, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        var database = DatabaseService.Database(context, databaseId);
        if (paths.Count is 0 or > 4 || paths.Any(path => path.Length is 0 or > 128 || path.Any(char.IsControl))) throw new AccountAccessException("Choose one to four distinct dotted field paths.", 422);
        collection = ValidCollection(collection);
        var scope = DatabaseService.Scope(context);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        try { await backend.CreateIndexAsync(scope, home, database.Id, collection, paths, cancellationToken); }
        catch (DatabaseProviderException error) when (error.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Treat only the actual matching provider index as an idempotent
            // replay; other conflicts must still reach the caller.
            if (!(await backend.IndexesAsync(scope, home, database.Id, cancellationToken)).Any(index => index.Collection == collection && index.Fields.SequenceEqual(paths))) throw;
        }
    }

    public async Task DropIndexAsync(AccountContext context, string databaseId, string name, CancellationToken cancellationToken)
    {
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        var database = DatabaseService.Database(context, databaseId);
        if (name.Length is 0 or > 128 || name.Any(char.IsControl)) throw new AccountAccessException("Choose an index.", 422);
        await backend.DropIndexAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Id, name, cancellationToken);
    }

    public static string ValidCollection(string collection)
    {
        if (collection.Length is 0 or > 128 || collection.Contains('/') || collection.Any(char.IsControl)) throw new AccountAccessException("Enter a collection name without slashes or line breaks.", 422);
        return collection;
    }
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex CollectionPattern();
}

public sealed record DocumentBrowse(IReadOnlyList<TerraBaseDocument> Documents, int Matched, int Page, int PageCount, bool More);
public sealed record SqlCatalog(IReadOnlyList<string> Keywords, IReadOnlyList<SqlCatalogCollection> Collections);
public sealed record SqlCatalogCollection(string Name, long Documents, bool Approximate, string Source, IReadOnlyList<SqlCatalogField> Fields);
public sealed record SqlCatalogField(string Path, string Type);
