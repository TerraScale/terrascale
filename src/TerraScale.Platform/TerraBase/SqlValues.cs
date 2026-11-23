using System.Globalization;

namespace TerraScale.Platform.TerraBase;

public static class SqlValues
{
    public static JsonElement Null { get; } = JsonSerializer.SerializeToElement<string?>(null, PlatformJsonContext.Default.String);

    public static JsonElement Text(string value) => JsonSerializer.SerializeToElement(value, PlatformJsonContext.Default.String);

    public static JsonElement OptionalText(string? value) => value is null ? Null : Text(value);

    public static JsonElement Number(long value) => JsonSerializer.SerializeToElement(value, PlatformJsonContext.Default.Int64);

    public static JsonElement OptionalNumber(long? value) => value is { } present ? Number(present) : Null;

    public static SqlHttpStatement Statement(string sql, params IReadOnlyList<JsonElement> args) => new(sql, args, null);

    public static SqlExecuteRequest Batch(params IReadOnlyList<SqlHttpStatement> statements) => new(statements);
}

public readonly record struct SqlRow(IReadOnlyList<JsonElement> Values)
{
    public string Text(int index) => Values[index] switch
    {
        { ValueKind: JsonValueKind.String } value => value.GetString() ?? throw new InvalidDataException("a text column held null"),
        { ValueKind: JsonValueKind.Number } value => value.GetRawText(),
        var other => throw new InvalidDataException($"column {index} is {other.ValueKind}, expected text"),
    };

    public string? OptionalText(int index) => Values[index].ValueKind == JsonValueKind.Null ? null : Text(index);

    public long Number(int index) => Values[index] switch
    {
        { ValueKind: JsonValueKind.Number } value when value.TryGetInt64(out var number) => number,
        { ValueKind: JsonValueKind.Number } value => (long)value.GetDouble(),
        { ValueKind: JsonValueKind.String } value when long.TryParse(value.GetString(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var parsed) => parsed,
        var other => throw new InvalidDataException($"column {index} is {other.ValueKind}, expected an integer"),
    };

    public long? OptionalNumber(int index) => Values[index].ValueKind == JsonValueKind.Null ? null : Number(index);

    public JsonElement Raw(int index) => Values[index];
}

public static class SqlResultExtensions
{
    extension(SqlHttpStatementResult result)
    {
        public IEnumerable<SqlRow> RowsOf() => result.Rows.Select(static row => new SqlRow(row));

        public SqlRow? FirstRow() => result.Rows.Count == 0 ? null : new SqlRow(result.Rows[0]);
    }

    extension(DateTimeOffset at)
    {
        public long UnixMs() => at.ToUnixTimeMilliseconds();
    }

    extension(long unixMs)
    {
        public DateTimeOffset FromUnixMs() => DateTimeOffset.FromUnixTimeMilliseconds(unixMs);
    }
}
