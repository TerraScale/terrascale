using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TerraScale.DotNet.TerraBase;

/// <summary>
/// A SQL argument: null, a 64-bit integer, a finite double, a boolean (stored as 1 or 0) or text. Values convert
/// implicitly, so <c>[42, "Ada", true, null]</c> is an argument list.
/// </summary>
[JsonConverter(typeof(TerraBaseSqlValueConverter))]
public readonly record struct TerraBaseSqlValue
{
    private TerraBaseSqlValue(object? value) => Value = value;

    /// <summary>SQL NULL.</summary>
    public static TerraBaseSqlValue Null => default;

    /// <summary>The <see cref="long"/>, <see cref="double"/>, <see cref="bool"/> or <see cref="string"/> sent, or null.</summary>
    public object? Value { get; }

    /// <summary>An integer argument.</summary>
    public static implicit operator TerraBaseSqlValue(long value) => new(value);

    /// <summary>An integer argument.</summary>
    public static implicit operator TerraBaseSqlValue(int value) => new((long)value);

    /// <summary>A finite real argument; NaN and infinities are refused.</summary>
    public static implicit operator TerraBaseSqlValue(double value) =>
        double.IsFinite(value) ? new(value) : throw new ArgumentOutOfRangeException(nameof(value), "SQL arguments must be finite numbers.");

    /// <summary>A boolean argument, stored by TerraBase as 1 or 0.</summary>
    public static implicit operator TerraBaseSqlValue(bool value) => new(value);

    /// <summary>A text argument; null is SQL NULL.</summary>
    public static implicit operator TerraBaseSqlValue(string? value) => new(value);
}

/// <summary>One statement of a SQL request.</summary>
/// <param name="Sql">Statement text, at most 16 KiB. BEGIN, COMMIT and ROLLBACK are refused: every request is one transaction.</param>
/// <param name="Args">Positional arguments for <c>?</c> or <c>?NNN</c> parameters.</param>
/// <param name="Named">Named arguments, keyed without their <c>:</c>, <c>@</c> or <c>$</c> prefix.</param>
public sealed record TerraBaseSqlStatement(string Sql, IReadOnlyList<TerraBaseSqlValue>? Args = null, IReadOnlyDictionary<string, TerraBaseSqlValue>? Named = null);

/// <summary>A result column.</summary>
/// <param name="Name">Column name.</param>
/// <param name="Decltype">Declared type of a table column, or null for an expression.</param>
public sealed record TerraBaseSqlColumn(string Name, string? Decltype = null);

/// <summary>One statement's result.</summary>
/// <param name="Columns">Result columns; empty for most writes.</param>
/// <param name="Rows">Rows in result order.</param>
/// <param name="RowsAffected">Rows a write changed.</param>
/// <param name="LastInsertRowid">The integer key of the last inserted row, when the table has one.</param>
public sealed record TerraBaseSqlStatementResult(IReadOnlyList<TerraBaseSqlColumn> Columns, IReadOnlyList<TerraBaseSqlRecord> Rows, long RowsAffected, long? LastInsertRowid);

/// <summary>
/// One result row: each value is a JSON null, number or string. Integers arrive as JSON numbers; TerraBase writes
/// them exactly, but clients that read JSON numbers as doubles (JavaScript) lose precision beyond 2^53.
/// </summary>
public sealed class TerraBaseSqlRecord
{
    private readonly JsonElement values;
    private readonly IReadOnlyDictionary<string, int> ordinals;

    internal TerraBaseSqlRecord(IReadOnlyList<TerraBaseSqlColumn> columns, IReadOnlyDictionary<string, int> ordinals, JsonElement values)
    {
        Columns = columns;
        this.ordinals = ordinals;
        this.values = values;
    }

    /// <summary>The result's columns.</summary>
    public IReadOnlyList<TerraBaseSqlColumn> Columns { get; }

    /// <summary>The value at <paramref name="ordinal"/>.</summary>
    public JsonElement this[int ordinal] => ordinal >= 0 && ordinal < Columns.Count ? values[ordinal] : throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <summary>The value of the first column named <paramref name="name"/>.</summary>
    public JsonElement this[string name] => values[GetOrdinal(name)];

    /// <summary>The ordinal of the first column named <paramref name="name"/> (case-sensitive).</summary>
    public int GetOrdinal(string name) =>
        ordinals.TryGetValue(name, out var ordinal) ? ordinal : throw new ArgumentException($"The result has no column '{name}'.", nameof(name));

    /// <summary>Whether the value is SQL NULL.</summary>
    public bool IsNull(int ordinal) => this[ordinal].ValueKind == JsonValueKind.Null;

    /// <inheritdoc cref="IsNull(int)"/>
    public bool IsNull(string name) => IsNull(GetOrdinal(name));

    /// <summary>The value as an integer; throws <see cref="InvalidOperationException"/> or <see cref="FormatException"/> for other values.</summary>
    public long GetInt64(int ordinal) => this[ordinal].GetInt64();

    /// <inheritdoc cref="GetInt64(int)"/>
    public long GetInt64(string name) => GetInt64(GetOrdinal(name));

    /// <summary>The value as a double; throws <see cref="InvalidOperationException"/> for non-numbers.</summary>
    public double GetDouble(int ordinal) => this[ordinal].GetDouble();

    /// <inheritdoc cref="GetDouble(int)"/>
    public double GetDouble(string name) => GetDouble(GetOrdinal(name));

    /// <summary>The value as a boolean: a nonzero integer is true; throws for non-integers.</summary>
    public bool GetBoolean(int ordinal) => GetInt64(ordinal) != 0;

    /// <inheritdoc cref="GetBoolean(int)"/>
    public bool GetBoolean(string name) => GetBoolean(GetOrdinal(name));

    /// <summary>The value as text; throws <see cref="InvalidOperationException"/> for numbers and NULL.</summary>
    public string GetString(int ordinal) =>
        this[ordinal] is { ValueKind: JsonValueKind.String } text ? text.GetString()! : throw new InvalidOperationException($"Column {ordinal} is not text.");

    /// <inheritdoc cref="GetString(int)"/>
    public string GetString(string name) => GetString(GetOrdinal(name));

    /// <summary>The row as a column-name map; a repeated name keeps its first value.</summary>
    public IReadOnlyDictionary<string, JsonElement> ToDictionary() => ordinals.ToDictionary(pair => pair.Key, pair => values[pair.Value], StringComparer.Ordinal);
}

/// <summary>
/// A refused SQL statement (<c>{"error":{"code","message","statement","sqlite_code"}}</c>). The whole request rolled back.
/// <see cref="Code"/> is <c>sql_error</c> (400), <c>forbidden_scope</c> (403: writes and DDL need documents:write),
/// <c>sql_conflict</c> (409, SQLITE_BUSY) or <c>sql_too_large</c> (413). Never retried automatically. Other failures on
/// the SQL route (401, 404, 429, 503, <c>invalid_request</c>) are the generic <see cref="TerraBaseException"/> subclasses.
/// </summary>
public sealed class TerraBaseSqlException : TerraBaseException
{
    internal TerraBaseSqlException(HttpStatusCode statusCode, string message, string code, int statement, string sqliteCode)
        : base(statusCode, message, code, statement)
    {
        Code = code;
        Statement = statement;
        SqliteCode = sqliteCode;
    }

    /// <summary>TerraBase's SQL error code, the same as <see cref="TerraBaseException.ErrorCode"/>.</summary>
    public string Code { get; }

    /// <summary>Zero-based index of the failing statement.</summary>
    public int Statement { get; }

    /// <summary>The SQLite error code, such as <c>SQLITE_CONSTRAINT_PRIMARYKEY</c>.</summary>
    public string SqliteCode { get; }
}

internal sealed record TerraBaseSqlExecuteRequest(IReadOnlyList<TerraBaseSqlStatement> Statements);

internal sealed record TerraBaseSqlExecuteResponse(IReadOnlyList<TerraBaseSqlStatementBody> Results) : IJsonOnDeserialized
{
    void IJsonOnDeserialized.OnDeserialized() => TerraBaseJsonContext.RequireNonNullItems(Results);
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
            || Rows.EnumerateArray().Any(row => row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != Columns.Count
                || row.EnumerateArray().Any(cell => cell.ValueKind is not (JsonValueKind.Null or JsonValueKind.Number or JsonValueKind.String))))
            throw new JsonException("Each SQL row holds one null, number or string per column.");
    }

    public TerraBaseSqlStatementResult ToResult()
    {
        var ordinals = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < Columns.Count; i++) ordinals.TryAdd(Columns[i].Name, i);
        var lookup = ordinals.AsReadOnly();
        return new(Columns, [.. Rows.EnumerateArray().Select(row => new TerraBaseSqlRecord(Columns, lookup, row))], RowsAffected, LastInsertRowid);
    }
}

internal sealed class TerraBaseSqlValueConverter : JsonConverter<TerraBaseSqlValue>
{
    public override TerraBaseSqlValue Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw new NotSupportedException("SQL arguments are request-only.");

    public override void Write(Utf8JsonWriter writer, TerraBaseSqlValue value, JsonSerializerOptions options)
    {
        switch (value.Value)
        {
            case long integer: writer.WriteNumberValue(integer); break;
            case double real: writer.WriteNumberValue(real); break;
            case bool flag: writer.WriteBooleanValue(flag); break;
            case string text: writer.WriteStringValue(text); break;
            default: writer.WriteNullValue(); break;
        }
    }
}

public sealed partial class TerraBaseDatabase
{
    /// <summary>The most statements one SQL request carries.</summary>
    public const int MaxSqlStatements = 256;

    /// <summary>
    /// Runs one relational SQL statement as its own transaction. Reads need documents:read; writes and DDL need
    /// documents:write. Sent once; a refusal throws <see cref="TerraBaseSqlException"/>.
    /// </summary>
    public Task<TerraBaseSqlStatementResult> ExecuteSqlAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args = null, CancellationToken cancellationToken = default) =>
        ExecuteSqlAsync(new TerraBaseSqlStatement(sql, args), cancellationToken);

    /// <inheritdoc cref="ExecuteSqlAsync(string, IReadOnlyList{TerraBaseSqlValue}?, CancellationToken)"/>
    public Task<TerraBaseSqlStatementResult> ExecuteSqlAsync(string sql, IReadOnlyDictionary<string, TerraBaseSqlValue> named, CancellationToken cancellationToken = default) =>
        ExecuteSqlAsync(new TerraBaseSqlStatement(sql, Named: named), cancellationToken);

    /// <inheritdoc cref="ExecuteSqlAsync(string, IReadOnlyList{TerraBaseSqlValue}?, CancellationToken)"/>
    public async Task<TerraBaseSqlStatementResult> ExecuteSqlAsync(TerraBaseSqlStatement statement, CancellationToken cancellationToken = default) =>
        (await ExecuteSqlBatchAsync([statement], cancellationToken).ConfigureAwait(false))[0];

    /// <summary>Runs one SQL statement and returns its rows. See <see cref="ExecuteSqlAsync(TerraBaseSqlStatement, CancellationToken)"/>.</summary>
    public async Task<IReadOnlyList<TerraBaseSqlRecord>> QuerySqlAsync(string sql, IReadOnlyList<TerraBaseSqlValue>? args = null, CancellationToken cancellationToken = default) =>
        (await ExecuteSqlAsync(sql, args, cancellationToken).ConfigureAwait(false)).Rows;

    /// <inheritdoc cref="QuerySqlAsync(string, IReadOnlyList{TerraBaseSqlValue}?, CancellationToken)"/>
    public async Task<IReadOnlyList<TerraBaseSqlRecord>> QuerySqlAsync(string sql, IReadOnlyDictionary<string, TerraBaseSqlValue> named, CancellationToken cancellationToken = default) =>
        (await ExecuteSqlAsync(sql, named, cancellationToken).ConfigureAwait(false)).Rows;

    /// <summary>
    /// Runs 1 to 256 statements as one transaction, all or nothing, and returns one result per statement in order
    /// (body at most 1 MiB). Sent once; a refused statement rolls everything back and throws
    /// <see cref="TerraBaseSqlException"/> naming its index.
    /// </summary>
    public async Task<IReadOnlyList<TerraBaseSqlStatementResult>> ExecuteSqlBatchAsync(IReadOnlyList<TerraBaseSqlStatement> statements, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (statements.Count is 0 or > MaxSqlStatements) throw new ArgumentException($"A SQL request holds 1 to {MaxSqlStatements} statements.", nameof(statements));
        foreach (var statement in statements)
        {
            ArgumentNullException.ThrowIfNull(statement, nameof(statements));
            ArgumentException.ThrowIfNullOrWhiteSpace(statement.Sql, nameof(statements));
            if (statement.Named?.Keys.FirstOrDefault(name => name.Length == 0 || name[0] is ':' or '@' or '$') is { } prefixed)
                throw new ArgumentException($"Named argument '{prefixed}' must be non-empty and written without its :, @ or $ prefix.", nameof(statements));
        }

        var (response, status) = await connection.SendAsync(HttpMethod.Post, path + "/sql/execute",
            TerraBaseConnection.Json(new TerraBaseSqlExecuteRequest(statements), Json.TerraBaseSqlExecuteRequest), Json.TerraBaseSqlExecuteResponse, null, cancellationToken).ConfigureAwait(false);
        return response.Results.Count == statements.Count
            ? [.. response.Results.Select(result => result.ToResult())]
            : throw new TerraBaseException(status, "TerraBase returned a result count that does not match the statements.", "invalid_response", null);
    }
}
