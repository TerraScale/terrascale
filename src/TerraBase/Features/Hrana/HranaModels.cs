using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Hrana;

/// <summary>A Hrana pipeline body: the stream's baton (null opens a stream) and its requests.</summary>
internal sealed record HranaPipelineRequest(string? Baton, IReadOnlyList<HranaRequest> Requests);

/// <summary>
/// One stream request, flat: <c>type</c> selects which members apply (<c>execute</c>: stmt; <c>batch</c>: batch;
/// <c>sequence</c> and <c>describe</c>: sql or sql_id; <c>store_sql</c>: sql_id and sql; <c>close_sql</c>: sql_id).
/// </summary>
internal sealed record HranaRequest(string Type, HranaStmt? Stmt = null, HranaBatch? Batch = null, string? Sql = null, int? SqlId = null);

/// <summary>A statement: its text or a stored sql_id, with positional and named arguments.</summary>
internal sealed record HranaStmt(
    string? Sql = null, int? SqlId = null, IReadOnlyList<HranaValue>? Args = null, IReadOnlyList<HranaNamedArg>? NamedArgs = null, bool? WantRows = null);

/// <summary>A named argument; the name may carry its <c>:</c>, <c>@</c> or <c>$</c> prefix.</summary>
internal sealed record HranaNamedArg(string Name, HranaValue Value);

/// <summary>A wire value: <c>null</c>, <c>integer</c> (decimal string), <c>float</c> (number), <c>text</c> or <c>blob</c> (base64).</summary>
internal sealed record HranaValue(string Type, JsonElement? Value = null, string? Base64 = null);

/// <summary>Batch steps, each optionally guarded by a condition on earlier steps.</summary>
internal sealed record HranaBatch(IReadOnlyList<HranaBatchStep> Steps);

/// <summary>One batch step.</summary>
internal sealed record HranaBatchStep(HranaStmt Stmt, HranaCondition? Condition = null);

/// <summary>A step condition: <c>ok</c>/<c>error</c> (step), <c>not</c> (cond), <c>and</c>/<c>or</c> (conds), <c>is_autocommit</c>.</summary>
internal sealed record HranaCondition(string Type, int? Step = null, HranaCondition? Cond = null, IReadOnlyList<HranaCondition>? Conds = null);

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

/// <summary>A response of the request's type; <c>result</c> holds a <see cref="HranaStmtResult"/>, <see cref="HranaBatchResult"/> or <see cref="HranaDescribeResult"/>.</summary>
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

/// <summary>One entry per step in both lists; a skipped step is null in both.</summary>
internal sealed record HranaBatchResult(IReadOnlyList<HranaStmtResult?> StepResults, IReadOnlyList<HranaError?> StepErrors);

/// <summary>A statement's parameters and columns.</summary>
internal sealed record HranaDescribeResult(IReadOnlyList<HranaParam> Params, IReadOnlyList<HranaCol> Cols, bool IsExplain, bool IsReadonly);

/// <summary>A parameter name with its prefix, or null for <c>?</c>.</summary>
internal sealed record HranaParam(string? Name);

/// <summary>A Hrana error; <c>code</c> is always set.</summary>
internal sealed record HranaError(string Message, string Code)
{
    public static HranaError From(SqlError error) => new(error.Message, error.Code);
}

/// <summary>
/// A request-level refusal: libSQL clients read <c>message</c> and <c>code</c>; the Go client reads <c>error</c>.
/// </summary>
internal sealed record HranaErrorBody(string Error, string Message, string Code);

/// <summary>A cursor body: the stream's baton and one batch.</summary>
internal sealed record HranaCursorRequest(string? Baton, HranaBatch Batch);

/// <summary>The cursor's first line: the baton that continues the stream; no base URL.</summary>
internal sealed record HranaCursorHead(string? Baton, string? BaseUrl);

/// <summary>A cursor line: <c>step_begin</c> (step, cols), <c>row</c> (row), <c>step_error</c> (step, error) or <c>error</c> (error).</summary>
internal sealed record HranaCursorEntry(
    string Type,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] int? Step = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<HranaCol>? Cols = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] IReadOnlyList<SqlValue>? Row = null,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] HranaError? Error = null);

/// <summary>
/// A cursor <c>step_end</c>. Unlike a pipeline result, the Rust client reads this <c>last_insert_rowid</c> as a JSON
/// number, not a string.
/// </summary>
internal sealed record HranaStepEnd(string Type, long AffectedRowCount, long? LastInsertRowid);

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
[JsonSerializable(typeof(HranaBatchResult))]
[JsonSerializable(typeof(HranaDescribeResult))]
[JsonSerializable(typeof(HranaErrorBody))]
[JsonSerializable(typeof(HranaCursorRequest))]
[JsonSerializable(typeof(HranaCursorHead))]
[JsonSerializable(typeof(HranaCursorEntry))]
[JsonSerializable(typeof(HranaStepEnd))]
internal sealed partial class HranaJsonContext : JsonSerializerContext;
