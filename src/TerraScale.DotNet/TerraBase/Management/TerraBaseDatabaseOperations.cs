using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>One document row of the read-only SQL subset.</summary>
/// <param name="Id">Document key.</param>
/// <param name="Value">Whole document for *, otherwise selected fields; missing fields are null.</param>
public sealed record TerraBaseSqlRow(string Id, JsonElement Value);

/// <summary>Read-only SQL result bounded by the server's LIMIT or 8 MiB of JSON.</summary>
/// <param name="Matched">Rows returned.</param>
/// <param name="Truncated">Whether either result bound cut rows short.</param>
/// <param name="Rows">Rows in query order.</param>
public sealed record TerraBaseSqlResult(int Matched, bool Truncated, IReadOnlyList<TerraBaseSqlRow> Rows) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Rows);
}

/// <summary>A secondary index built before the create response.</summary>
/// <param name="Name">Index name, unique per database.</param>
/// <param name="Collection">Collection name.</param>
/// <param name="Fields">Key fields in order.</param>
/// <param name="State">active in the pinned producer.</param>
public sealed record TerraBaseIndex(string Name, string Collection, IReadOnlyList<string> Fields, string State) : IJsonOnDeserialized
{
    /// <summary>Index kind (btree or text); null when an older producer omits it. Future kinds pass through.</summary>
    public string? Kind { get; init; }

    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Fields);
}

/// <summary>Secondary indexes in name order.</summary>
/// <param name="Items">Indexes in this database.</param>
public sealed record TerraBaseIndexList(IReadOnlyList<TerraBaseIndex> Items) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Items);
}

internal sealed record TerraBaseSqlRequest(string Sql);
internal sealed record TerraBaseCreateIndexRequest(string Collection, IReadOnlyList<string> Fields, string? Name, string? Kind);

public sealed partial class TerraBaseDatabase
{
    /// <summary>Branches a consistent snapshot in the source database's home region. Requires databases:write; sent once.</summary>
    public async Task<TerraBaseManagedDatabase> BranchAsync(string name, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return (await connection.SendAsync(HttpMethod.Post, path + "/branches", TerraBaseConnection.Json(new RequestBody { Name = name }, Json.RequestBody),
            Json.TerraBaseManagedDatabase, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Runs the read-only SQL subset with databases:read. Work-budget exhaustion is 400; never retries.</summary>
    public async Task<TerraBaseSqlResult> RunSqlAsync(string sql, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(sql);
        return (await connection.SendAsync(HttpMethod.Post, path + "/sql", TerraBaseConnection.Json(new TerraBaseSqlRequest(sql), Json.TerraBaseSqlRequest),
            Json.TerraBaseSqlResult, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Lists secondary indexes, optionally for one collection. Requires databases:read.</summary>
    public async Task<TerraBaseIndexList> ListIndexesAsync(string? collection = null, CancellationToken cancellationToken = default) =>
        (await connection.SendAsync(HttpMethod.Get, path + "/indexes" + TerraBaseConnection.Query(("collection", collection)), null,
            Json.TerraBaseIndexList, null, cancellationToken).ConfigureAwait(false)).Value;

    /// <summary>Builds an index with 1–4 distinct dotted field paths; name is optional. Requires databases:write; sent once.</summary>
    public Task<TerraBaseIndex> CreateIndexAsync(string collection, IReadOnlyList<string> fields, string? name = null, CancellationToken cancellationToken = default) =>
        CreateIndexCoreAsync(collection, fields, name, kind: null, cancellationToken);

    /// <summary>Builds a btree or text index with 1–4 distinct dotted field paths. Null kind uses the producer's btree default. Requires databases:write; sent once.</summary>
    public Task<TerraBaseIndex> CreateIndexAsync(string collection, string? kind, IReadOnlyList<string> fields, string? name = null, CancellationToken cancellationToken = default) =>
        CreateIndexCoreAsync(collection, fields, name, kind, cancellationToken);

    private async Task<TerraBaseIndex> CreateIndexCoreAsync(string collection, IReadOnlyList<string> fields, string? name, string? kind, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrEmpty(collection);
        ArgumentNullException.ThrowIfNull(fields);
        if (kind is not (null or "btree" or "text")) throw new ArgumentException("kind must be btree or text.", nameof(kind));
        return (await connection.SendAsync(HttpMethod.Post, path + "/indexes", TerraBaseConnection.Json(new TerraBaseCreateIndexRequest(collection, fields, name, kind), Json.TerraBaseCreateIndexRequest),
            Json.TerraBaseIndex, null, cancellationToken).ConfigureAwait(false)).Value;
    }

    /// <summary>Drops a secondary index by name. Requires databases:write; sent once.</summary>
    public Task DropIndexAsync(string name, CancellationToken cancellationToken = default) =>
        connection.SendAsync(HttpMethod.Delete, path + "/indexes/" + TerraBaseConnection.Segment(name, nameof(name)), null, null, cancellationToken);
}
