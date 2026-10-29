using System.Text.Json;
using System.Text.RegularExpressions;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Dashboard.Features.Data;

public sealed partial class DataService(DatabaseService databases, IDocumentBackend documents, IDatabaseBackend backend)
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

    public async Task<DocumentBrowse> BrowseAsync(AccountContext context, string databaseId, string collection, string search, string sort, bool descending, int page, CancellationToken cancellationToken, string cursor = "", int pageSize = 25)
    {
        if (pageSize is < 1 or > 100) throw new AccountAccessException("Document limits use 1–100 rows.", 422);
        collection = ValidCollection(collection);
        var database = DatabaseService.Database(context, databaseId);
        var path = sort.Length == 0 ? "$id" : "/" + sort.Replace("~", "~0", StringComparison.Ordinal).Replace("/", "~1", StringComparison.Ordinal);
        if (sort.Length > 128) throw new AccountAccessException("Sort field is too long.", 422);
        if (System.Text.Encoding.UTF8.GetByteCount(cursor) > 1024 * 1024) throw new AccountAccessException("The document continuation cursor exceeds 1 MiB.", 422);
        if (page > 1 && cursor.Length == 0) throw new AccountAccessException("Use the document continuation link to open the next page.", 422);
        if (cursor.Length > 0 && (string.IsNullOrWhiteSpace(cursor) || cursor.Any(char.IsControl))) throw new AccountAccessException("Enter a valid document continuation cursor.", 422);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        page = Math.Clamp(page, 1, int.MaxValue - 1);
        search = search.Trim();
        var rows = new List<TerraBaseDocument>();
        var seen = new HashSet<string>(StringComparer.Ordinal) { cursor };
        var query = new TerraBaseQuery { Sort = [new(path, descending ? "desc" : "asc")] };
        var next = cursor;
        var scanned = 0;
        // At most four reads and 100 scanned documents per response. Limit by remaining display
        // slots so every match is returned before advancing the producer's opaque cursor.
        for (var read = 0; read < (search.Length == 0 ? 1 : 4); read++)
        {
            var result = await documents.DocumentsAsync(DatabaseService.Scope(context), home, database.Name, collection, query with { Limit = Math.Min(pageSize - rows.Count, 100 - scanned), Cursor = next.Length == 0 ? null : next }, cancellationToken);
            scanned += result.Items.Count;
            rows.AddRange(result.Items.Where(row => search.Length == 0 || (row.Id + " " + row.Document.GetRawText()).Contains(search, StringComparison.OrdinalIgnoreCase)));
            next = result.NextCursor ?? "";
            if (next.Length > 0 && !seen.Add(next)) throw new HttpRequestException("Document pagination did not advance.");
            if (next.Length == 0 || rows.Count == pageSize || scanned == 100) break;
        }
        return new(rows, rows.Count, page, next.Length > 0 ? page + 1 : page, next.Length > 0, cursor, next, scanned);
    }

    public async Task<IReadOnlyList<TerraBaseDocument>> DocumentsAsync(AccountContext context, string databaseId, string collection, CancellationToken cancellationToken)
        => await ListAsync(context, databaseId, collection, "", 100, "", cancellationToken);

    public async Task<IReadOnlyList<TerraBaseDocument>> ListAsync(AccountContext context, string databaseId, string collection, string search, int limit, string after, CancellationToken cancellationToken)
        => (await ListPageAsync(context, databaseId, collection, search, limit, after, cancellationToken)).Items;

    public async Task<TerraBaseDocumentPage> ListPageAsync(AccountContext context, string databaseId, string collection, string search, int limit, string after, CancellationToken cancellationToken)
    {
        if (limit == 0) limit = 100;
        if (limit is < 1 or > 100) throw new AccountAccessException("Document limits use 1–100 rows.", 422);
        var database = DatabaseService.Database(context, databaseId);
        var result = await documents.ListAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Name,
            ValidCollection(collection), limit, after.Length == 0 ? null : after, cancellationToken);
        if (after.Length > 0 && result.NextCursor == after) throw new HttpRequestException("Document pagination did not advance.");
        search = search.Trim();
        // Search filters this native page only. Its cursor must survive even when no rows match.
        return new([.. result.Items.Where(row => search.Length == 0 || (row.Id + " " + row.Document.GetRawText()).Contains(search, StringComparison.OrdinalIgnoreCase))], result.NextCursor);
    }

    public async Task CreateCollectionAsync(AccountContext context, string databaseId, string collection, CancellationToken cancellationToken)
    {
        context = await databases.CurrentAsync(context, cancellationToken);
        if (!context.CanWriteProjectData) throw new AccountAccessException("Write access is required.");
        if (!CollectionPattern().IsMatch(collection)) throw new AccountAccessException("Collection names start with a lowercase letter and use up to 63 lowercase letters, digits, hyphens, and underscores.", 422);
        var database = DatabaseService.Database(context, databaseId);
        await documents.CreateCollectionAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Name, collection, cancellationToken);
    }

    public async Task<TerraBaseWriteResult> WriteAsync(AccountContext context, string databaseId, string collection, string operation, string id, string body, string version, CancellationToken cancellationToken)
    {
        context = await databases.CurrentAsync(context, cancellationToken);
        if (!context.CanWriteProjectData) throw new AccountAccessException("Write access is required.");
        var database = DatabaseService.Database(context, databaseId);
        collection = ValidCollection(collection);
        if (operation is not ("create" or "replace" or "patch" or "delete") || operation != "create" && id.Length == 0)
            throw new AccountAccessException("Choose a document and an operation.", 422);
        if (operation != "create" || id.Length > 0) ValidDocumentId(id);
        JsonElement document = default;
        if (operation != "delete")
        {
            try
            {
                using var parsed = JsonDocument.Parse(body);
                var compact = JsonDraft.Compact(parsed.RootElement, htmlSafe: false);
                if (System.Text.Encoding.UTF8.GetByteCount(compact) > 1024 * 1024) throw new AccountAccessException("Enter a valid JSON document of 1 MiB or less. Size uses compact UTF-8 JSON.", 422);
                using var transport = JsonDocument.Parse(compact);
                document = transport.RootElement.Clone();
            }
            catch (Exception error) when (error is JsonException or InvalidOperationException) { throw new AccountAccessException("Enter a valid JSON document of 1 MiB or less.", 422); }
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
        try { return await documents.WriteAsync(scope, home, database.Name, collection, operation, id, document, version, cancellationToken); }
        // A create carries no version, so TerraBase's create-only precondition failing means the ID is taken.
        catch (HttpRequestException error) when (operation == "create" && error.StatusCode is System.Net.HttpStatusCode.Conflict or System.Net.HttpStatusCode.PreconditionFailed)
        { throw new AccountAccessException("A document with this ID already exists. Choose another ID or omit it to generate one.", 409); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.RequestEntityTooLarge)
        { throw new AccountAccessException("The encoded document exceeds the data API's 1 MiB request limit. Reduce its size and try again. Your draft has been kept.", 413); }
    }

    public async Task<TerraBaseDocument> GetAsync(AccountContext context, string databaseId, string collection, string id, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        collection = ValidCollection(collection);
        ValidDocumentId(id);
        return await documents.GetAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Name, collection, id, cancellationToken)
            ?? throw new AccountAccessException("Document is unavailable.", 404);
    }

    public async Task<TerraBaseCollectionSchema> SchemaAsync(AccountContext context, string databaseId, string collection, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        collection = ValidCollection(collection);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        var scope = DatabaseService.Scope(context);
        return await documents.SchemaAsync(scope, home, database.Name, collection, cancellationToken);
    }

    public async Task DropCollectionAsync(AccountContext context, string databaseId, string collection, string confirmation, CancellationToken cancellationToken)
    {
        context = await databases.CurrentAsync(context, cancellationToken);
        if (!context.CanWriteProjectData) throw new AccountAccessException("Write access is required.");
        var database = DatabaseService.Database(context, databaseId);
        collection = ValidCollection(collection);
        if (confirmation != collection) throw new AccountAccessException("Type the collection name to confirm deletion.", 422);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        await documents.DropCollectionAsync(DatabaseService.Scope(context), home, database.Name, collection, cancellationToken);
    }

    public async Task<(string Name, IAsyncEnumerable<TerraBaseExportLine> Lines)> ExportAsync(AccountContext context, string databaseId, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        return (database.Name, documents.ExportAsync(DatabaseService.Scope(context), home, database.Name, cancellationToken));
    }

    // One statement through TerraBase's relational SQL API, as its own transaction. Reads need read access; writes and DDL
    // need the same write access as document writes, and callers that promise read-only (MCP) refuse them outright.
    public async Task<DatabaseSqlResult> SqlAsync(AccountContext context, string databaseId, string statement, bool allowWrites, CancellationToken cancellationToken)
    {
        context = await databases.CurrentAsync(context, cancellationToken);
        var database = DatabaseService.Database(context, databaseId);
        statement = statement.Trim();
        if (SqlValidation.Diagnostics(statement).FirstOrDefault(item => item.Severity == "error") is { } problem) throw new AccountAccessException(problem.Message, 422);
        var write = !SqlValidation.IsRead(statement);
        if (write && !allowWrites) throw new AccountAccessException("Only read-only statements (SELECT, WITH, EXPLAIN, VALUES, introspection PRAGMA) run here.", 422);
        if (write && !context.CanWriteProjectData) throw new AccountAccessException("Write access is required to change data or schema. Your role can run read-only statements.");
        return await backend.SqlAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Id, statement, write, cancellationToken);
    }

    public async Task<SqlCatalog> CatalogAsync(AccountContext context, string databaseId, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        var tables = await backend.SqlTablesAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Id, cancellationToken);
        return new(SqlCatalog.Empty.Keywords, [.. tables.Tables.Select(table => new SqlCatalogTable(table.Name,
            [.. table.Columns.Select(column => new SqlCatalogField(column.Name, string.IsNullOrEmpty(column.Decltype) ? "No declared type" : column.Decltype))]))], tables.Truncated);
    }

    public async Task<IReadOnlyList<DatabaseIndex>> IndexesAsync(AccountContext context, string databaseId, CancellationToken cancellationToken)
    {
        var database = DatabaseService.Database(context, databaseId);
        return await backend.IndexesAsync(DatabaseService.Scope(context), await databases.HomeAsync(context, database, cancellationToken), database.Id, cancellationToken);
    }

    public async Task CreateIndexAsync(AccountContext context, string databaseId, string collection, string fields, CancellationToken cancellationToken)
        => await CreateIndexFieldsAsync(context, databaseId, collection, [.. fields.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Distinct(StringComparer.Ordinal)], cancellationToken);

    public async Task CreateIndexFieldAsync(AccountContext context, string databaseId, string collection, string field, CancellationToken cancellationToken)
        => await CreateIndexFieldsAsync(context, databaseId, collection, [field.Trim()], cancellationToken);

    private async Task CreateIndexFieldsAsync(AccountContext context, string databaseId, string collection, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        context = await databases.CurrentAsync(context, cancellationToken);
        if (!context.CanManage) throw new AccountAccessException("Organization management access is required.");
        var database = DatabaseService.Database(context, databaseId);
        if (paths.Count is 0 or > 4 || paths.Any(path => path.Length is 0 or > 128 || path.Any(char.IsControl))) throw new AccountAccessException("Choose one to four distinct dotted field paths.", 422);
        collection = ValidCollection(collection);
        var scope = DatabaseService.Scope(context);
        var home = await databases.HomeAsync(context, database, cancellationToken);
        try { await backend.CreateIndexAsync(scope, home, database.Id, collection, paths, cancellationToken); }
        catch (HttpRequestException error) when (error.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            // Treat only the actual matching provider index as an idempotent
            // replay; other conflicts must still reach the caller.
            if (!(await backend.IndexesAsync(scope, home, database.Id, cancellationToken)).Any(index => index.Collection == collection && index.Fields.SequenceEqual(paths))) throw;
        }
    }

    public async Task DropIndexAsync(AccountContext context, string databaseId, string name, CancellationToken cancellationToken)
    {
        context = await databases.CurrentAsync(context, cancellationToken);
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

    public const string DocumentIdRule = "Document IDs are 26-character uppercase ULIDs (Crockford Base32, first character 0-7), such as 01J9Z3K8M4Q7R2T5V6W8X9Y0ZA.";

    public static string ValidDocumentId(string id) =>
        TerraScale.DotNet.TerraScaleIds.IsValid(id) ? id : throw new AccountAccessException(DocumentIdRule, 422);
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,62}$", RegexOptions.CultureInvariant)]
    private static partial Regex CollectionPattern();
}

public sealed record DocumentBrowse(IReadOnlyList<TerraBaseDocument> Documents, int Matched, int Page, int PageCount, bool More,
    string Cursor = "", string NextCursor = "", int Scanned = 0);
public sealed record SqlCatalog(IReadOnlyList<string> Keywords, IReadOnlyList<SqlCatalogTable> Tables, bool Truncated = false)
{
    public static SqlCatalog Empty { get; } = new(["SELECT", "FROM", "WHERE", "AND", "OR", "NOT", "IN", "IS", "NULL", "ORDER BY", "ASC", "DESC", "LIMIT", "TRUE", "FALSE"], []);
}
public sealed record SqlCatalogTable(string Name, IReadOnlyList<SqlCatalogField> Fields);
public sealed record SqlCatalogField(string Path, string Type);
