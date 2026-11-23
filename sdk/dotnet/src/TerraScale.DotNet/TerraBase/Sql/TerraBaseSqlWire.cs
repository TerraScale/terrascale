using System.Diagnostics;
using System.Net;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

internal sealed record TerraBaseSqlExecuteRequest(IReadOnlyList<TerraBaseSqlStatement> Statements);

internal sealed record TerraBaseSqlExecuteResponse(IReadOnlyList<TerraBaseSqlStatementBody> Results, string? Bookmark = null, string? Consistency = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Results);

    public IReadOnlyList<TerraBaseSqlStatementResult> ToResults(int expected, HttpStatusCode? status) =>
        Results.Count == expected
            ? [.. Results.Select(result => result.ToResult())]
            : throw new TerraBaseException(status, "TerraBase returned a result count that does not match the statements.", "invalid_response", null);
}

internal sealed record TerraBaseSqlColumnBody(string Name, string? Decltype = null, string? Type = null);

internal sealed record TerraBaseSqlStatementBody(
    IReadOnlyList<TerraBaseSqlColumnBody> Columns,
    TerraBaseSqlRows Rows,
    [property: JsonPropertyName("rows_affected")] long? SnakeRowsAffected = null,
    [property: JsonPropertyName("last_insert_rowid")] long? SnakeLastInsertRowid = null,
    [property: JsonPropertyName("rowsAffected")] long? RowsAffected = null,
    [property: JsonPropertyName("lastInsertRowId")] long? LastInsertRowId = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        TerraBaseJsonContext.RequireNonNullItems(Columns);
        if (!Rows.Fits(Columns.Count)) throw new JsonException("Each SQL row holds one null, number, string or blob per column.");
        if ((SnakeRowsAffected ?? RowsAffected) is null) throw new JsonException("A SQL result names rowsAffected or rows_affected.");
    }

    public TerraBaseSqlStatementResult ToResult()
    {
        IReadOnlyList<TerraBaseSqlColumn> columns = [.. Columns.Select(static column => new TerraBaseSqlColumn(column.Name, column.Decltype ?? column.Type))];
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < columns.Count; i++) ordinals.TryAdd(columns[i].Name, i);
        var lookup = ordinals.AsReadOnly();
        var records = new TerraBaseSqlRecord[Rows.Count];
        for (var i = 0; i < records.Length; i++) records[i] = new TerraBaseSqlRecord(columns, lookup, Rows.Cells, i * columns.Count);
        return (SnakeRowsAffected, RowsAffected) switch
        {
            ({ } affected, _) => new(columns, records, affected, SnakeLastInsertRowid),
            (null, { } affected) => new(columns, records, affected, LastInsertRowId),
            (null, null) => throw new UnreachableException(),
        };
    }
}

internal sealed record TerraBaseSqlSocketRequest(long Id, string Type, IReadOnlyList<TerraBaseSqlStatement>? Statements = null, string? Consistency = null, string? Bookmark = null);

internal sealed record TerraBaseSqlSocketError(string Code, string Message, int Statement, string SqlState)
{
    public TerraBaseSqlException ToException(Exception? inner = null) => new(null, Message, Code, Statement, SqlState, inner);
}

internal sealed record TerraBaseSqlSocketAnswer(long? Id, string Type, bool Transaction, IReadOnlyList<TerraBaseSqlStatementBody>? Results = null,
    TerraBaseSqlSocketError? Error = null, string? Bookmark = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        if (Type is not ("result" or "error") || (Type == "result" ? Results is null : Error is null)) throw new JsonException("A SQL frame is a result or an error.");
        if (Results is not null) TerraBaseJsonContext.RequireNonNullItems(Results);
    }
}

[JsonSourceGenerationOptions(JsonSerializerDefaults.Web, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(TerraBaseSqlExecuteRequest))]
[JsonSerializable(typeof(TerraBaseSqlExecuteResponse))]
[JsonSerializable(typeof(TerraBaseSqlSocketRequest))]
[JsonSerializable(typeof(TerraBaseSqlSocketAnswer))]
internal sealed partial class TerraBaseSqlJsonContext : JsonSerializerContext
{
    public static TerraBaseSqlJsonContext Wire { get; } = new(new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    });
}
