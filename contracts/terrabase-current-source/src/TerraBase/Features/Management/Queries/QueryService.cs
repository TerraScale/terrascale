using System.Buffers;
using System.Text;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Features.Management.Usage;
using TerraBase.Features.Memory;
using TerraBase.Hosting;
using TerraBase.Primitives;
using TerraBase.Tenancy;
using static TerraBase.Features.Management.SystemCatalog;

namespace TerraBase.Features.Management.Queries;

/// <remarks>
/// Statements run on the managed database's storage; usage records and saved queries live in <see cref="SystemCatalog"/>
/// under the database and project state, so deleting the database or project deletes them.
/// </remarks>
internal sealed class QueryService(IStorageEngine engine, SystemCatalog catalog, QueryFieldRecorder recorder, IUsageMeter usage, TimeProvider time,
    IQueryShapeTracker shapes, IMemoryBudget memory) : IQueryService
{
    // Bounds the complete serialized response, including rows and the result envelope.
    private const long MaxResultBytes = 8 * 1024 * 1024;

    private const long MaxCatalogPageBytes = 1024 * 1024;
    private const string QuerySequence = "saved_query_sequence";

    // Same code as a document query over the engine read budget.
    private static readonly Validation TooExpensive = new("query too expensive", "invalid_query");

    public async ValueTask<RunSqlResult> RunAsync(TenantContext database, SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);
        ArgumentNullException.ThrowIfNull(statement);
        cancellationToken.ThrowIfCancellationRequested();
        string? shape;
        IMemoryReservation? retained = null;
        using (var scratch = memory.TryReserve(MemoryLane.Foreground, 64 * 1024))
        {
            shape = scratch is null ? null : QueryShape.Normalize(statement.Query);
            if (shape is not null)
            {
                retained = memory.TryReserve(MemoryLane.Foreground, 32L + 2L * shape.Length);
            }
        }

        using var reservation = retained;
        if (reservation is null)
        {
            shape = null;
        }
        var observation = new QueryObservation();
        var started = time.GetTimestamp();
        List<IndexSuggestion> suggestions = [];
        try
        {
            var rows = await ReadAsync(
                database,
                async transaction =>
                {
                    suggestions = Suggest(statement.Query, await transaction.ListIndexesAsync(cancellationToken).ConfigureAwait(false));
                    return await RowsAsync(transaction, statement, observation, cancellationToken).ConfigureAwait(false);
                },
                new SqlResultBody(0, false, []),
                cancellationToken).ConfigureAwait(false);
            // One read per run the engine answered, as a data API query page; each row bills its value's started 4 KiB.
            usage.RecordRead(database, rows.Rows.Sum(row => OperationUnits.Of(Encoding.UTF8.GetByteCount(row.Value.GetRawText()))));
            return rows;
        }
        catch (QueryCapacityException)
        {
            return TooExpensive;
        }
        finally
        {
            // Also when the work budget stopped the run: those are the statements an index helps most.
            recorder.Record(database, statement.Query.Collection, suggestions.Select(suggestion => suggestion.Field));
            shapes.Record(database, statement.Query.Collection, shape ?? string.Empty, time.GetElapsedTime(started), observation.RowsScanned);
        }
    }

    public async ValueTask<CheckSqlResult> CheckAsync(TenantContext database, SqlStatement statement, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(statement);
        SqlQueryPlan? plan;
        IReadOnlyList<IndexDefinition> indexes;
        try
        {
            (plan, indexes) = await ReadAsync<(SqlQueryPlan? Plan, IReadOnlyList<IndexDefinition> Indexes)>(
                database,
                async transaction => (
                    await transaction.ExplainSqlAsync(statement.Query, cancellationToken).ConfigureAwait(false),
                    await transaction.ListIndexesAsync(cancellationToken).ConfigureAwait(false)),
                (null, []),
                cancellationToken).ConfigureAwait(false);
        }
        catch (QueryCapacityException)
        {
            return TooExpensive;
        }

        if (plan is null)
        {
            return new NotFound();
        }

        return new SqlCheckBody(true, null, statement.Query.Collection, statement.HasLimit, plan.Steps, plan.Indexes, Suggest(statement.Query, indexes));
    }

    public ValueTask<ListSavedQueriesResult> ListSavedAsync(Project project, string? after, int limit, CancellationToken cancellationToken) =>
        catalog.ReadAsync<ListSavedQueriesResult>(async transaction =>
        {
            var page = await transaction.ListAsync(SavedQueryOrder(project), after, limit, MaxCatalogPageBytes, cancellationToken).ConfigureAwait(false);
            var queries = new List<SavedQuery>(page.Documents.Count);
            var next = page.HasMore ? page.Documents[^1].Id : null;
            long bytes = 0;
            foreach (var item in page.Documents)
            {
                var queryId = JsonSerializer.Deserialize(item.Json.Span, TerraBaseJsonContext.Default.String)!;
                var stored = await transaction.GetAsync(SavedQueries(project), queryId, cancellationToken).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("the saved query order names a missing query");
                // The order entries are tiny; the queries they name bound the page too, beyond the first.
                if (queries.Count > 0 && bytes + stored.Json.Length > MaxCatalogPageBytes)
                {
                    next = page.Documents[queries.Count - 1].Id;
                    break;
                }

                bytes += stored.Json.Length;
                queries.Add(EntityPayload.Read(stored, TerraBaseJsonContext.Default.SavedQueryEntry)!.Query);
            }

            return new ManagementPage<SavedQuery>(queries, next);
        }, static error => error, cancellationToken);

    public async ValueTask<CreateSavedQueryResult> CreateSavedAsync(string userId, Project project, string name, string sql, CancellationToken cancellationToken)
    {
        return await catalog.WriteAsync<CreateSavedQueryResult>(async transaction =>
        {
            if (!await IsWriterProjectAsync(transaction, userId, project, cancellationToken).ConfigureAwait(false))
            {
                return new NotFound();
            }

            var now = time.GetUtcNow();
            var query = new SavedQuery(EntityIds.New(time).ToString(), name, sql, now, null);
            // Lists follow the persisted per-organization sequence, so they keep creation order across restarts and clock steps.
            var order = EngineManagementService.AscendingKey(await EngineManagementService.NextSequenceAsync(
                transaction, EngineManagementService.State(project.OrganizationId), QuerySequence, cancellationToken).ConfigureAwait(false));
            await PutAsync(transaction, SavedQueries(project), query.Id, new SavedQueryEntry(query, order), TerraBaseJsonContext.Default.SavedQueryEntry, cancellationToken).ConfigureAwait(false);
            await PutAsync(transaction, SavedQueryOrder(project), order, query.Id, TerraBaseJsonContext.Default.String, cancellationToken).ConfigureAwait(false);
            return query;
        }, static error => error, cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask<GetSavedQueryResult> GetSavedAsync(Project project, string queryId, CancellationToken cancellationToken) =>
        await catalog.ReadAsync<GetSavedQueryResult>(
            async transaction => await GetAsync(transaction, SavedQueries(project), queryId, TerraBaseJsonContext.Default.SavedQueryEntry, cancellationToken)
                .ConfigureAwait(false) is { } entry
                ? entry.Query
                : new NotFound(),
            static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<UpdateSavedQueryResult> UpdateSavedAsync(
        string userId, Project project, string queryId, string name, string sql, CancellationToken cancellationToken) =>
        await catalog.WriteAsync<UpdateSavedQueryResult>(async transaction =>
        {
            if (!await IsWriterProjectAsync(transaction, userId, project, cancellationToken).ConfigureAwait(false)
                || await MemberEntryAsync(transaction, userId, project, queryId, cancellationToken).ConfigureAwait(false) is not { } entry)
            {
                return new NotFound();
            }

            var query = entry.Query with { Name = name, Sql = sql, UpdatedAt = time.GetUtcNow() };
            await PutAsync(transaction, SavedQueries(project), query.Id, entry with { Query = query }, TerraBaseJsonContext.Default.SavedQueryEntry, cancellationToken).ConfigureAwait(false);
            return query;
        }, static error => error, cancellationToken).ConfigureAwait(false);

    public async ValueTask<DeleteSavedQueryResult> DeleteSavedAsync(string userId, Project project, string queryId, CancellationToken cancellationToken) =>
        await catalog.WriteAsync<DeleteSavedQueryResult>(async transaction =>
        {
            if (!await IsWriterProjectAsync(transaction, userId, project, cancellationToken).ConfigureAwait(false)
                || await MemberEntryAsync(transaction, userId, project, queryId, cancellationToken).ConfigureAwait(false) is not { } entry)
            {
                return new NotFound();
            }

            await transaction.DeleteAsync(SavedQueries(project), queryId, cancellationToken).ConfigureAwait(false);
            await transaction.DeleteAsync(SavedQueryOrder(project), entry.Order, cancellationToken).ConfigureAwait(false);
            return new Done();
        }, static error => error, cancellationToken).ConfigureAwait(false);

    /// <summary>Filtered or sorted fields of <paramref name="query"/> that no index of its collection leads with.</summary>
    internal static List<IndexSuggestion> Suggest(SqlQuery query, IReadOnlyList<IndexDefinition> indexes)
    {
        var filtered = new HashSet<DocumentField>();
        Collect(query.Filter, filtered);
        var sorted = query.Order.Select(order => order.Field).ToHashSet();
        return [.. filtered.Concat(sorted).Distinct()
            .Where(field => !field.IsMetadata && !indexes.Any(index => index.Collection == query.Collection && index.Fields[0] == field))
            .Select(field => new IndexSuggestion(field.Path, (filtered.Contains(field), sorted.Contains(field)) switch
            {
                (true, true) => "filtered and sorted without an index",
                (true, false) => "filtered without an index",
                _ => "sorted without an index",
            }))];
    }

    private static void Collect(SqlFilter? filter, HashSet<DocumentField> fields)
    {
        switch (filter)
        {
            case SqlCompareFilter compare:
                fields.Add(compare.Field);
                break;
            case SqlNotFilter not:
                Collect(not.Filter, fields);
                break;
            case SqlAndFilter and:
                foreach (var child in and.Filters)
                {
                    Collect(child, fields);
                }

                break;
            case SqlOrFilter or:
                foreach (var child in or.Filters)
                {
                    Collect(child, fields);
                }

                break;
        }
    }

    // A read transaction on the database's storage; empty when the storage does not exist.
    private async ValueTask<T> ReadAsync<T>(
        TenantContext database, Func<IStorageTransaction, ValueTask<T>> read, T empty, CancellationToken cancellationToken)
    {
        if (await engine.BeginTransactionAsync(database, write: false, cancellationToken).ConfigureAwait(false) is not { } transaction)
        {
            return empty;
        }

        await using (transaction.ConfigureAwait(false))
        {
            return await read(transaction).ConfigureAwait(false);
        }
    }

    // Rows are projected as the engine streams them, so the output budget (not stored sizes) decides truncation, and only
    // one stored document is held besides the rows kept. The engine bounds the work: past it the run fails with a
    // QueryCapacityException, also while Turso is still scanning or sorting before the first row.
    private static async ValueTask<SqlResultBody> RowsAsync(
        IStorageTransaction transaction, SqlStatement statement, QueryObservation observation, CancellationToken cancellationToken)
    {
        var query = statement.Query;
        var rows = new List<SqlRow>();
        // Reserve the longest possible envelope (the most rows; false is longer than true). The rows' commas
        // are counted separately; serializing the empty result keeps this aligned with the response metadata.
        long bytes = JsonSerializer.SerializeToUtf8Bytes(new SqlResultBody(query.Limit, false, []), TerraBaseJsonContext.Default.SqlResultBody).Length;
        // One document past the limit tells whether the limit cut the rows short.
        await foreach (var document in transaction.QuerySqlAsync(query with { Limit = query.Limit + 1 }, cancellationToken, observation).ConfigureAwait(false))
        {
            var separator = rows.Count == 0 ? 0 : 1;
            if (rows.Count == query.Limit || Row(document, query.Collection, statement.Projection, MaxResultBytes - bytes - separator, cancellationToken) is not { } row)
            {
                return new SqlResultBody(rows.Count, true, rows);
            }

            bytes += row.Bytes + separator;
            rows.Add(row.Row);
        }

        return new SqlResultBody(rows.Count, false, rows);
    }

    // The row and the full {id,value} row's size as the response encodes it (including escaping), or null once it
    // passes budget: checked after every field, so one row can never pass the budget by much.
    private static (SqlRow Row, long Bytes)? Row(
        StoredDocument document, string collection, IReadOnlyList<DocumentField>? projection, long budget, CancellationToken cancellationToken)
    {
        using var json = JsonDocument.Parse(document.Json);
        var buffer = new ArrayBufferWriter<byte>();
        int start, end;
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("id", document.Id);
            writer.WritePropertyName("value");
            writer.Flush();
            start = buffer.WrittenCount;
            if (projection is null)
            {
                json.RootElement.WriteTo(writer);
            }
            else
            {
                // Each visited object is scanned once, rather than once per projected field. The cache lives for
                // this one bounded stored document; TryAdd preserves the first occurrence of duplicate members.
                var members = new Dictionary<string, Dictionary<string, JsonElement>>(StringComparer.Ordinal);
                writer.WriteStartObject();
                foreach (var field in projection)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    writer.WritePropertyName(field.Path);
                    if (field == DocumentField.Id)
                    {
                        writer.WriteStringValue(document.Id);
                    }
                    else if (field == DocumentField.Version)
                    {
                        // The version the data API reports for this document state, never the raw commit offset.
                        writer.WriteStringValue(ETags.Version(document.Scope, collection, document.Id, document.Version, document.Json));
                    }
                    else if (Member(json.RootElement, field, members, cancellationToken) is { } value)
                    {
                        value.WriteTo(writer);
                    }
                    else
                    {
                        writer.WriteNullValue();
                    }

                    writer.Flush();
                    if (buffer.WrittenCount > budget)
                    {
                        return null;
                    }
                }

                writer.WriteEndObject();
            }

            writer.Flush();
            end = buffer.WrittenCount;
            writer.WriteEndObject();
        }

        // Only the value is parsed back: it is no deeper than the stored document, which parsed above.
        return buffer.WrittenCount > budget
            ? null
            : (new SqlRow(document.Id, JsonElement.Parse(buffer.WrittenSpan[start..end])), buffer.WrittenCount);
    }

    // The members of each object reached so far, keyed by its dotted path from the root ("" for the root): field members
    // are identifiers, so the path names one object, and each object is enumerated once per row however many fields read it.
    private static JsonElement? Member(
        JsonElement value, DocumentField field, Dictionary<string, Dictionary<string, JsonElement>> cache,
        CancellationToken cancellationToken)
    {
        var path = "";
        foreach (var member in field.Members)
        {
            if (value.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            if (!cache.TryGetValue(path, out var members))
            {
                members = [with(StringComparer.Ordinal)];
                foreach (var property in value.EnumerateObject())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    // The first of duplicate members, like the engine's json_extract.
                    members.TryAdd(property.Name, property.Value);
                }

                cache.Add(path, members);
            }

            if (!members.TryGetValue(member, out value))
            {
                return null;
            }

            path = path.Length == 0 ? member : $"{path}.{member}";
        }

        return value;
    }

    // Rechecked inside the write: the project, or the user's membership, may be gone since the request started.
    private static async ValueTask<bool> IsMemberProjectAsync(IStorageTransaction transaction, string userId, Project project, CancellationToken cancellationToken) =>
        await EngineManagementService.GetProjectAsync(transaction, userId, project.OrganizationId, project.Id, cancellationToken).ConfigureAwait(false) is not null;

    // Inside a saved query write: the user may still change the project (a viewer may not), and the change advances the
    // organization's version.
    private static async ValueTask<bool> IsWriterProjectAsync(IStorageTransaction transaction, string userId, Project project, CancellationToken cancellationToken)
    {
        if (!await IsMemberProjectAsync(transaction, userId, project, cancellationToken).ConfigureAwait(false)
            || await EngineManagementService.WritableAsync(transaction, userId, project.OrganizationId, cancellationToken).ConfigureAwait(false) is not OrganizationMember)
        {
            return false;
        }

        await EngineManagementService.TouchOrganizationAsync(transaction, project.OrganizationId, cancellationToken).ConfigureAwait(false);
        return true;
    }

    // The saved query, when the user is still a member of its project, read inside the write.
    private static async ValueTask<SavedQueryEntry?> MemberEntryAsync(
        IStorageTransaction transaction, string userId, Project project, string queryId, CancellationToken cancellationToken) =>
        await IsMemberProjectAsync(transaction, userId, project, cancellationToken).ConfigureAwait(false)
            ? await GetAsync(transaction, SavedQueries(project), queryId, TerraBaseJsonContext.Default.SavedQueryEntry, cancellationToken).ConfigureAwait(false)
            : null;

    private static string SavedQueries(Project project) => ProjectState(project.Id) + "saved_queries";

    private static string SavedQueryOrder(Project project) => ProjectState(project.Id) + "saved_query_order";
}

/// <summary>A saved query as stored, with its key in the project's creation-order list.</summary>
/// <param name="Query">The saved query.</param>
/// <param name="Order">Persisted per-organization sequence, allocated in the query's write transaction.</param>
internal sealed record SavedQueryEntry(SavedQuery Query, string Order);
