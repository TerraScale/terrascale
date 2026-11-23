using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace TerraScale.DotNet.TerraBase;

public sealed record TerraBaseSqlStatement(string Sql, IReadOnlyList<TerraBaseSqlValue>? Args = null, IReadOnlyDictionary<string, TerraBaseSqlValue>? Named = null)
{
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

public sealed record TerraBaseSqlColumn(string Name, string? Decltype = null);

public sealed record TerraBaseSqlStatementResult(IReadOnlyList<TerraBaseSqlColumn> Columns, IReadOnlyList<TerraBaseSqlRecord> Rows, long RowsAffected, long? LastInsertRowid = null)
{
    public IReadOnlyList<T> As<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        return [.. Rows.Select(row => row.As(typeInfo))];
    }
}

public sealed class TerraBaseSqlRecord
{
    private readonly TerraBaseSqlCell[] cells;
    private readonly int offset;
    private readonly IReadOnlyDictionary<string, int> ordinals;

    internal TerraBaseSqlRecord(IReadOnlyList<TerraBaseSqlColumn> columns, IReadOnlyDictionary<string, int> ordinals, TerraBaseSqlCell[] cells, int offset)
    {
        Columns = columns;
        this.ordinals = ordinals;
        this.cells = cells;
        this.offset = offset;
    }

    public IReadOnlyList<TerraBaseSqlColumn> Columns { get; }

    public JsonElement this[int ordinal] => Element(Cell(ordinal));

    public JsonElement this[string name] => this[GetOrdinal(name)];

    public int GetOrdinal(string name) =>
        ordinals.TryGetValue(name, out var ordinal) ? ordinal : throw new ArgumentException($"The result has no column '{name}'.", nameof(name));

    public bool IsNull(int ordinal) => Cell(ordinal).Kind == JsonValueKind.Null;

    public bool IsNull(string name) => IsNull(GetOrdinal(name));

    public long GetInt64(int ordinal) => Cell(ordinal) switch
    {
        { IsInteger: true } cell => cell.Integer,
        { RealText: not null } => throw new FormatException($"Column {ordinal} is not an integer."),
        _ => throw new InvalidOperationException($"Column {ordinal} is not a number."),
    };

    public long GetInt64(string name) => GetInt64(GetOrdinal(name));

    public double GetDouble(int ordinal) => Cell(ordinal) switch
    {
        { IsInteger: true } cell => cell.Integer,
        { RealText: { } real } => double.Parse(real, NumberStyles.Float, CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException($"Column {ordinal} is not a number."),
    };

    public double GetDouble(string name) => GetDouble(GetOrdinal(name));

    public bool GetBoolean(int ordinal) => GetInt64(ordinal) != 0;

    public bool GetBoolean(string name) => GetBoolean(GetOrdinal(name));

    public string GetString(int ordinal) =>
        Cell(ordinal) is { Text: { } text } ? text : throw new InvalidOperationException($"Column {ordinal} is not text.");

    public string GetString(string name) => GetString(GetOrdinal(name));

    public byte[] GetBytes(int ordinal) =>
        Cell(ordinal) is { Blob: { } blob } ? [.. blob] : throw new InvalidOperationException($"Column {ordinal} is not a blob.");

    public byte[] GetBytes(string name) => GetBytes(GetOrdinal(name));

    public IReadOnlyDictionary<string, JsonElement> ToDictionary() => ordinals.ToDictionary(pair => pair.Key, pair => this[pair.Value], StringComparer.Ordinal);

    public T As<T>(JsonTypeInfo<T> typeInfo)
    {
        ArgumentNullException.ThrowIfNull(typeInfo);
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, ordinal) in ordinals)
            {
                writer.WritePropertyName(name);
                Cell(ordinal).WriteTo(writer, blobAsObject: false);
            }

            writer.WriteEndObject();
        }

        return JsonSerializer.Deserialize(buffer.WrittenSpan, typeInfo) ?? throw new JsonException("A row cannot map to null.");
    }

    internal TerraBaseSqlCell Cell(int ordinal) =>
        ordinal >= 0 && ordinal < Columns.Count ? cells[offset + ordinal] : throw new ArgumentOutOfRangeException(nameof(ordinal));

    private static JsonElement Element(TerraBaseSqlCell cell)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            cell.WriteTo(writer, blobAsObject: true);
        }

        var reader = new Utf8JsonReader(buffer.WrittenSpan);
        return JsonElement.ParseValue(ref reader);
    }
}
