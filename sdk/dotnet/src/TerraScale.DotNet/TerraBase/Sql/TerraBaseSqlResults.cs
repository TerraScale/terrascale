using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

/// <summary>One SQL statement.</summary>
/// <param name="Sql">Statement text, at most 16 KiB.</param>
/// <param name="Args">Positional arguments for <c>?</c> or <c>?NNN</c> parameters.</param>
/// <param name="Named">Named arguments, keyed without their <c>:</c>, <c>@</c> or <c>$</c> prefix.</param>
public sealed record TerraBaseSqlStatement(string Sql, IReadOnlyList<TerraBaseSqlValue>? Args = null, IReadOnlyDictionary<string, TerraBaseSqlValue>? Named = null)
{
    /// <summary>The most statements one request or frame carries.</summary>
    public const int MaxPerRequest = 256;

    internal static void Validate(IReadOnlyList<TerraBaseSqlStatement> statements)
    {
        ArgumentNullException.ThrowIfNull(statements);
        if (statements.Count is 0 or > MaxPerRequest) throw new ArgumentException($"A SQL request holds 1 to {MaxPerRequest} statements.", nameof(statements));
        foreach (var statement in statements)
        {
            ArgumentNullException.ThrowIfNull(statement, nameof(statements));
            ArgumentException.ThrowIfNullOrWhiteSpace(statement.Sql, nameof(statements));
            if (statement.Named?.Keys.FirstOrDefault(name => name.Length == 0 || name[0] is ':' or '@' or '$') is { } prefixed)
                throw new ArgumentException($"Named argument '{prefixed}' must be non-empty and written without its :, @ or $ prefix.", nameof(statements));
        }
    }
}

/// <summary>A result column.</summary>
/// <param name="Name">Column name.</param>
/// <param name="Decltype">Declared type of a table column, or null for an expression.</param>
public sealed record TerraBaseSqlColumn(string Name, string? Decltype = null);

/// <summary>One statement's result.</summary>
/// <param name="Columns">Result columns; empty for most writes.</param>
/// <param name="Rows">Rows in result order.</param>
/// <param name="RowsAffected">Rows a write changed.</param>
/// <param name="LastInsertRowid">The integer key of the last inserted row, when the table has one.</param>
public sealed record TerraBaseSqlStatementResult(IReadOnlyList<TerraBaseSqlColumn> Columns, IReadOnlyList<TerraBaseSqlRecord> Rows, long RowsAffected, long? LastInsertRowid)
{
    /// <summary>Maps every row to <typeparamref name="T"/>; see <see cref="TerraBaseSqlRecord.As{T}"/>.</summary>
    public IReadOnlyList<T> As<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return [.. Rows.Select(row => row.As(typeInfo))];
    }
}

/// <summary>
/// One result row. Each value is a JSON null, number, string or a blob object <c>{"base64":"…"}</c>. Integers arrive
/// as exact JSON numbers.
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

    /// <summary>The raw value at <paramref name="ordinal"/>.</summary>
    public JsonElement this[int ordinal] => ordinal >= 0 && ordinal < Columns.Count ? values[ordinal] : throw new ArgumentOutOfRangeException(nameof(ordinal));

    /// <summary>The raw value of the first column named <paramref name="name"/>.</summary>
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

    /// <summary>The value as text; throws <see cref="InvalidOperationException"/> for numbers, blobs and NULL.</summary>
    public string GetString(int ordinal) =>
        this[ordinal] is { ValueKind: JsonValueKind.String } text ? text.GetString()! : throw new InvalidOperationException($"Column {ordinal} is not text.");

    /// <inheritdoc cref="GetString(int)"/>
    public string GetString(string name) => GetString(GetOrdinal(name));

    /// <summary>The value as a blob; throws <see cref="InvalidOperationException"/> for other values.</summary>
    public byte[] GetBytes(int ordinal) =>
        this[ordinal] is { ValueKind: JsonValueKind.Object } blob ? blob.GetProperty("base64").GetBytesFromBase64() : throw new InvalidOperationException($"Column {ordinal} is not a blob.");

    /// <inheritdoc cref="GetBytes(int)"/>
    public byte[] GetBytes(string name) => GetBytes(GetOrdinal(name));

    /// <summary>The row as a column-name map; a repeated name keeps its first value.</summary>
    public IReadOnlyDictionary<string, JsonElement> ToDictionary() => ordinals.ToDictionary(pair => pair.Key, pair => values[pair.Value], StringComparer.Ordinal);

    /// <summary>
    /// Maps the row to <typeparamref name="T"/> through source-generated JSON metadata: the row becomes a JSON object
    /// keyed by column name (a repeated name keeps its first value) and a blob becomes a base64 string, so it binds to a
    /// <see cref="byte"/> array. Name matching follows <paramref name="typeInfo"/>'s options.
    /// </summary>
    public T As<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, ordinal) in ordinals)
            {
                writer.WritePropertyName(name);
                var value = values[ordinal];
                if (value.ValueKind == JsonValueKind.Object) value.GetProperty("base64").WriteTo(writer);
                else value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize(buffer.WrittenSpan, typeInfo) ?? throw new JsonException("A row cannot map to null.");
    }
}
