using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

internal sealed record TerraBaseSqlExecuteRequest(IReadOnlyList<TerraBaseSqlStatement> Statements);

internal sealed record TerraBaseSqlExecuteResponse(IReadOnlyList<TerraBaseSqlStatementBody> Results) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Results);

    public IReadOnlyList<TerraBaseSqlStatementResult> ToResults(int expected, System.Net.HttpStatusCode? status) =>
        Results.Count == expected
            ? [.. Results.Select(result => result.ToResult())]
            : throw new TerraBaseException(status, "TerraBase returned a result count that does not match the statements.", "invalid_response", null);
}

internal sealed record TerraBaseSqlStatementBody(
    IReadOnlyList<TerraBaseSqlColumn> Columns,
    JsonElement Rows,
    [property: JsonPropertyName("rows_affected")] long RowsAffected,
    [property: JsonPropertyName("last_insert_rowid")] long? LastInsertRowid = null) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized()
    {
        TerraBaseJsonContext.RequireNonNullItems(Columns);
        if (Rows.ValueKind != JsonValueKind.Array
            || Rows.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != Columns.Count || row.EnumerateArray().Any(cell => !IsCell(cell))))
            throw new JsonException("Each SQL row holds one null, number, string or blob per column.");
    }

    private static bool IsCell(JsonElement cell) => cell.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Number or JsonValueKind.String => true,
        JsonValueKind.Object => cell.EnumerateObject().Count() == 1 && cell.TryGetProperty("base64", out var encoded)
            && encoded.ValueKind == JsonValueKind.String && encoded.TryGetBytesFromBase64(out _),
        _ => false,
    };

    public TerraBaseSqlStatementResult ToResult()
    {
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Columns.Count; i++) ordinals.TryAdd(Columns[i].Name, i);
        var lookup = ordinals.AsReadOnly();
        return new(Columns, [.. Rows.EnumerateArray().Select(row => new TerraBaseSqlRecord(Columns, lookup, row))], RowsAffected, LastInsertRowid);
    }
}

internal sealed record TerraBaseSqlSocketRequest(long Id, string Type, IReadOnlyList<TerraBaseSqlStatement>? Statements = null);

internal sealed record TerraBaseSqlSocketError(string Code, string Message, int Statement, string Sqlstate);

internal sealed record TerraBaseSqlSocketAnswer(long? Id, string Type, bool Transaction, IReadOnlyList<TerraBaseSqlStatementBody>? Results = null, TerraBaseSqlSocketError? Error = null)
    : IJsonOnDeserialized
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
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        RespectRequiredConstructorParameters = true,
        RespectNullableAnnotations = true,
    });
}
