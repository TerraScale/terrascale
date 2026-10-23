using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Hrana;

/// <summary>A Hrana pipeline body: the stream's baton (null opens a stream) and its requests.</summary>
internal sealed record HranaPipelineRequest(string? Baton, IReadOnlyList<HranaRequest> Requests);

/// <summary>
/// One stream request, flat: <c>type</c> selects which members apply (<c>execute</c>: stmt; <c>store_sql</c>: sql_id and sql; <c>close_sql</c>: sql_id).
/// </summary>
internal sealed record HranaRequest(string Type, HranaStmt? Stmt = null, string? Sql = null, int? SqlId = null);

/// <summary>A statement: its text or a stored sql_id, with positional and named arguments.</summary>
internal sealed record HranaStmt(
    string? Sql = null, int? SqlId = null, IReadOnlyList<HranaValue>? Args = null, IReadOnlyList<HranaNamedArg>? NamedArgs = null, bool? WantRows = null);

/// <summary>A named argument; the name may carry its <c>:</c>, <c>@</c> or <c>$</c> prefix.</summary>
internal sealed record HranaNamedArg(string Name, HranaValue Value);

/// <summary>A wire value: <c>null</c>, <c>integer</c> (decimal string), <c>float</c> (number), <c>text</c> or <c>blob</c> (base64).</summary>
internal sealed record HranaValue(string Type, JsonElement? Value = null, string? Base64 = null);

/// <summary>The pipeline answer: the next baton (null once closed), no base URL, one result per request.</summary>
internal sealed record HranaPipelineResponse(string? Baton, string? BaseUrl, IReadOnlyList<HranaResult> Results);

/// <summary><c>{"type":"ok","response":…}</c> or <c>{"type":"error","error":…}</c>.</summary>
internal sealed record HranaResult(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HranaResponse? Response = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HranaError? Error = null)
{
    public static HranaResult Ok(HranaResponse response) => new("ok", Response: response);

    public static HranaResult Failed(HranaError error) => new("error", Error: error);
}

/// <summary>A response of the request's type; <c>result</c> holds the request type's result.</summary>
internal sealed record HranaResponse(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] object? Result = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] bool? IsAutocommit = null);

/// <summary>A statement's columns, rows and counts; <c>last_insert_rowid</c> is always present.</summary>
internal sealed record HranaStmtResult(IReadOnlyList<HranaCol> Cols, IReadOnlyList<IReadOnlyList<SqlValue>> Rows, long AffectedRowCount, string? LastInsertRowid)
{
    public static HranaStmtResult From(SqlResult result, bool wantRows) => new(
        [.. result.Columns.Select(column => new HranaCol(column.Name, column.DeclType))],
        wantRows ? result.Rows : [],
        result.RowsAffected,
        result.LastInsertRowid?.ToString(CultureInfo.InvariantCulture));
}

/// <summary>A result column.</summary>
internal sealed record HranaCol(string? Name, string? Decltype);

/// <summary>A Hrana error; <c>code</c> is always set.</summary>
internal sealed record HranaError(string Message, string Code)
{
    public static HranaError From(SqlError error) => new(error.Message, error.Code);
}

/// <summary>
/// A request-level refusal: libSQL clients read <c>message</c> and <c>code</c>; the Go client reads <c>error</c>.
/// </summary>
internal sealed record HranaErrorBody(string Error, string Message, string Code);

/// <summary>Writes SQL values in Hrana's JSON form (integers as decimal strings).</summary>
internal sealed class HranaValueConverter : JsonConverter<SqlValue>
{
    public override SqlValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("Hrana values are parsed from HranaValue");

    public override void Write(Utf8JsonWriter writer, SqlValue value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        switch (value.Kind)
        {
            case SqlValueKind.Integer:
                writer.WriteString("type", "integer");
                writer.WriteString("value", value.Integer.ToString(CultureInfo.InvariantCulture));
                break;
            case SqlValueKind.Real:
                writer.WriteString("type", "float");
                writer.WritePropertyName("value");
                if (double.IsFinite(value.Real))
                {
                    writer.WriteNumberValue(value.Real);
                }
                else
                {
                    // JSON has no infinity; SQLite's own JSON renders it as an out-of-range literal the same way.
                    writer.WriteRawValue(value.Real > 0 ? "1e999" : "-1e999");
                }

                break;
            case SqlValueKind.Text:
                writer.WriteString("type", "text");
                writer.WriteString("value", value.Text);
                break;
            default:
                writer.WriteString("type", "null");
                break;
        }

        writer.WriteEndObject();
    }
}

/// <summary>Source-generated JSON metadata of the Hrana slice; responses escape only what JSON requires.</summary>
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    Converters = [typeof(HranaValueConverter)],
    MaxDepth = 64)]
[JsonSerializable(typeof(HranaPipelineRequest))]
[JsonSerializable(typeof(HranaPipelineResponse))]
[JsonSerializable(typeof(HranaStmtResult))]
[JsonSerializable(typeof(HranaErrorBody))]
internal sealed partial class HranaJsonContext : JsonSerializerContext;
