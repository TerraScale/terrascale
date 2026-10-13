using System.Buffers;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using TerraBase.Primitives;

namespace TerraBase.Features.Import;

/// <summary>How a column's values become JSON (docs/terrabase/import.md section 5).</summary>
internal enum ColumnMapping
{
    Unsupported,
    Integer,
    Float,
    DecimalString,
    Boolean,
    String,
    Uuid,
    Json,
    Base64,
    Date,
    Timestamp,
    Timestamptz,
    Text,

    /// <summary>SQLite: by each value's storage class.</summary>
    Storage,
}

/// <summary>A value that has no JSON form under its mapping.</summary>
internal sealed class ImportValueException(string code, string message, string? column) : Exception(message)
{
    public string Code { get; } = code;

    public string? Column { get; } = column;
}

/// <summary>Maps source types and rows to TerraBase documents, ids and checksums.</summary>
internal static partial class ImportMapping
{
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, MaxDepth = 64 };
    private static readonly JsonDocumentOptions JsonOptions = new() { MaxDepth = 64 };

    // A column value nests inside the row object; TerraBase stores at most 16 levels.
    private static readonly JsonDocumentOptions ValueOptions = new() { MaxDepth = 15 };

    /// <summary>The contract name of a mapping.</summary>
    public static string Name(ColumnMapping mapping) => mapping switch
    {
        ColumnMapping.Integer => "integer",
        ColumnMapping.Float => "float",
        ColumnMapping.DecimalString => "decimal_string",
        ColumnMapping.Boolean => "boolean",
        ColumnMapping.String or ColumnMapping.Uuid => "string",
        ColumnMapping.Json => "json",
        ColumnMapping.Base64 => "base64",
        ColumnMapping.Date => "date",
        ColumnMapping.Timestamp => "timestamp",
        ColumnMapping.Timestamptz => "timestamptz",
        ColumnMapping.Text => "text",
        ColumnMapping.Storage => "null",
        _ => "unsupported",
    };

    /// <summary>The mapping of a PostgreSQL type as pg_dump writes it.</summary>
    public static ColumnMapping Postgres(string type, Func<string, bool> isEnum)
    {
        if (type.Contains('[', StringComparison.Ordinal) || type.StartsWith("array", StringComparison.Ordinal))
        {
            return ColumnMapping.Unsupported;
        }

        // Drop type modifiers: numeric(14,4), character varying(200), timestamp(3) with time zone.
        var bare = TypeModifiers().Replace(type, "").Trim();
        if (isEnum(EnumKey(bare)))
        {
            return ColumnMapping.String;
        }

        return bare switch
        {
            "smallint" or "integer" or "bigint" or "int2" or "int4" or "int8" or "int" or "smallserial" or "serial" or "bigserial" => ColumnMapping.Integer,
            "real" or "double precision" or "float4" or "float8" or "float" => ColumnMapping.Float,
            "numeric" or "decimal" => ColumnMapping.DecimalString,
            "boolean" or "bool" => ColumnMapping.Boolean,
            "text" or "character varying" or "varchar" or "character" or "char" or "bpchar" or "name" or "\"char\"" => ColumnMapping.String,
            _ when bare == "citext" || bare.EndsWith(".citext", StringComparison.Ordinal) => ColumnMapping.String,
            "uuid" => ColumnMapping.Uuid,
            "json" or "jsonb" => ColumnMapping.Json,
            "bytea" => ColumnMapping.Base64,
            "date" => ColumnMapping.Date,
            "timestamp" or "timestamp without time zone" => ColumnMapping.Timestamp,
            "timestamp with time zone" or "timestamptz" => ColumnMapping.Timestamptz,
            "time" or "time without time zone" or "time with time zone" or "timetz" or "inet" or "cidr" or "macaddr" or "macaddr8"
                or "bit" or "bit varying" or "varbit" or "xml" => ColumnMapping.Text,
            _ when bare.StartsWith("interval", StringComparison.Ordinal) => ColumnMapping.Text,
            _ => ColumnMapping.Unsupported,
        };
    }

    /// <summary>A type name as an enum key: <c>public.mood</c>, <c>"My Type"</c> unquoted, schema <c>public</c> when unqualified.</summary>
    public static string EnumKey(string type)
    {
        var parts = ImportNames.Parse(type) ?? [];
        return parts.Count == 0 ? type : PostgresDump.Key(parts);
    }

    /// <summary>Whether a primary key may use this mapping.</summary>
    public static bool KeyAllowed(ColumnMapping mapping) =>
        mapping is ColumnMapping.Integer or ColumnMapping.DecimalString or ColumnMapping.Boolean or ColumnMapping.String or ColumnMapping.Uuid
            or ColumnMapping.Date or ColumnMapping.Timestamp or ColumnMapping.Timestamptz or ColumnMapping.Text or ColumnMapping.Storage;

    /// <summary>Writes one value under its mapping, or throws <see cref="ImportValueException"/>.</summary>
    public static void Write(Utf8JsonWriter writer, SourceValue value, ColumnMapping mapping, string column)
    {
        if (value.Kind == SourceKind.Null)
        {
            writer.WriteNullValue();
            return;
        }

        if (mapping == ColumnMapping.Storage)
        {
            WriteStorage(writer, value, column);
            return;
        }

        var text = value.Text!;
        switch (mapping)
        {
            case ColumnMapping.Integer when long.TryParse(text, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var integer):
                writer.WriteNumberValue(integer);
                return;
            case ColumnMapping.Float when FloatText().IsMatch(text) && ExactNumber.TryParse(Encoding.ASCII.GetBytes(text), out _):
                writer.WriteRawValue(text, skipInputValidation: false);
                return;
            case ColumnMapping.DecimalString when DecimalText().IsMatch(text):
            case ColumnMapping.String or ColumnMapping.Text:
                writer.WriteStringValue(text);
                return;
            case ColumnMapping.Uuid:
                writer.WriteStringValue(text.ToLowerInvariant());
                return;
            case ColumnMapping.Boolean when text is "t" or "f":
                writer.WriteBooleanValue(text == "t");
                return;
            case ColumnMapping.Json:
                WriteJson(writer, text, column);
                return;
            case ColumnMapping.Base64 when text.StartsWith("\\x", StringComparison.Ordinal) && (text.Length % 2) == 0 && IsHex(text.AsSpan(2)):
                writer.WriteBase64StringValue(Convert.FromHexString(text.AsSpan(2)));
                return;
            case ColumnMapping.Date or ColumnMapping.Timestamp or ColumnMapping.Timestamptz when Temporal(text, mapping) is { } converted:
                writer.WriteStringValue(converted);
                return;
            default:
                throw Invalid(column, mapping is ColumnMapping.Float ? "the number is NaN, infinite or outside TerraBase's range (nonzero magnitudes 1e-307 to below 1e308)"
                    : mapping is ColumnMapping.Date or ColumnMapping.Timestamp or ColumnMapping.Timestamptz ? "the value is infinite, BC or outside years 1 to 9999"
                    : $"the value is not a valid {Name(mapping)}");
        }
    }

    /// <summary>The id text of one key component, or throws <see cref="ImportValueException"/>.</summary>
    public static string KeyText(SourceValue value, ColumnMapping mapping, string column)
    {
        switch (value.Kind)
        {
            case SourceKind.Null:
                throw new ImportValueException("invalid_document_id", "a primary key value is NULL", column);
            case SourceKind.Integer:
                return value.Integer.ToString(CultureInfo.InvariantCulture);
            case SourceKind.Text when value.Text is { } text && mapping == ColumnMapping.Storage:
                return text;
            case SourceKind.Text when value.Text is not null:
                {
                    using var buffer = new PooledWriter();
                    Write(buffer.Writer, value, mapping, column);
                    buffer.Writer.Flush();
                    using var parsed = JsonDocument.Parse(buffer.Written);
                    return parsed.RootElement.ValueKind switch
                    {
                        JsonValueKind.String => parsed.RootElement.GetString()!,
                        JsonValueKind.True => "true",
                        JsonValueKind.False => "false",
                        _ => parsed.RootElement.GetRawText(),
                    };
                }

            default:
                throw new ImportValueException("invalid_document_id", "a primary key value is a REAL, BLOB or invalid text", column);
        }
    }

    /// <summary>A deterministic ULID for row <paramref name="row"/> of a keyless table: the job's time, randomness from SHA-256.</summary>
    public static string GeneratedId(string jobId, DateTimeOffset createdAt, string table, long row)
    {
        Span<byte> bytes = stackalloc byte[16];
        var time = createdAt.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--)
        {
            bytes[i] = (byte)(time & 0xff);
            time >>= 8;
        }

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{jobId}\0{table}\0{row.ToString(CultureInfo.InvariantCulture)}"));
        hash.AsSpan(0, 10).CopyTo(bytes[6..]);
        return new Ulid(bytes).ToString();
    }

    /// <summary>Checks a built document against TerraBase's rules, as a write would.</summary>
    public static void Check(ReadOnlyMemory<byte> json)
    {
        if (json.Length > DocumentJson.MaxBytes)
        {
            throw new ImportValueException("document_too_large", "the row's document exceeds 1 MiB", null);
        }

        try
        {
            _ = DocumentJson.Read(json, requireObject: true);
        }
        catch (DocumentValidationException error)
        {
            throw new ImportValueException(error.TooLarge ? "document_too_large" : "invalid_value", $"the row's document breaks TerraBase's rules: {error.Message}", null);
        }
    }

    /// <summary>
    /// One document's term of a table checksum: SHA-256 over its id and its canonical body. TerraBase may store a body in
    /// another byte layout than it was written in, so the body is canonical: members sorted by name, numbers as exact
    /// decimals (<see cref="ExactNumber"/>), strings re-escaped. Equal terms mean equal values as TerraBase compares them.
    /// </summary>
    public static void Add(Span<ulong> sum, string id, ReadOnlySpan<byte> body)
    {
        var canonical = new ArrayBufferWriter<byte>(body.Length + 16);
        using (var document = JsonDocument.Parse(body.ToArray(), JsonOptions))
        using (var writer = Writer(canonical))
        {
            WriteCanonical(writer, document.RootElement);
        }

        var stored = canonical.WrittenSpan;
        var buffer = ArrayPool<byte>.Shared.Rent(Encoding.UTF8.GetMaxByteCount(id.Length) + 1 + stored.Length);
        try
        {
            var length = Encoding.UTF8.GetBytes(id, buffer);
            buffer[length++] = 0;
            stored.CopyTo(buffer.AsSpan(length));
            Span<byte> hash = stackalloc byte[32];
            SHA256.HashData(buffer.AsSpan(0, length + stored.Length), hash);
            // A 256-bit big-endian sum, modulo 2^256: order-independent and sensitive to duplicates.
            ulong carry = 0;
            for (var word = 3; word >= 0; word--)
            {
                var term = System.Buffers.Binary.BinaryPrimitives.ReadUInt64BigEndian(hash[(word * 8)..]);
                var total = sum[word] + term;
                var overflow = total < term ? 1UL : 0;
                var withCarry = total + carry;
                overflow += withCarry < total ? 1UL : 0;
                sum[word] = withCarry;
                carry = overflow;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement value)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var member in value.EnumerateObject().OrderBy(member => member.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(member.Name);
                    WriteCanonical(writer, member.Value);
                }

                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in value.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }

                writer.WriteEndArray();
                break;
            case JsonValueKind.Number when ExactNumber.TryParse(Encoding.UTF8.GetBytes(value.GetRawText()), out var number):
                writer.WriteRawValue(number.Mantissa.ToString(CultureInfo.InvariantCulture) + "e" + number.Exponent.ToString(CultureInfo.InvariantCulture));
                break;
            default:
                value.WriteTo(writer);
                break;
        }
    }

    public static string Hex(ReadOnlySpan<ulong> sum)
    {
        var text = new StringBuilder(64);
        foreach (var word in sum)
        {
            text.Append(word.ToString("x16", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    public static Utf8JsonWriter Writer(IBufferWriter<byte> buffer) => new(buffer, WriterOptions);

    private static void WriteStorage(Utf8JsonWriter writer, SourceValue value, string column)
    {
        switch (value.Kind)
        {
            case SourceKind.Integer:
                writer.WriteNumberValue(value.Integer);
                return;
            case SourceKind.Real:
                {
                    var text = value.Real.ToString("R", CultureInfo.InvariantCulture);
                    if (!double.IsFinite(value.Real) || !ExactNumber.TryParse(Encoding.ASCII.GetBytes(text), out _))
                    {
                        throw Invalid(column, "the number is infinite or outside TerraBase's range (nonzero magnitudes 1e-307 to below 1e308)");
                    }

                    writer.WriteRawValue(text, skipInputValidation: false);
                    return;
                }

            case SourceKind.Text when value.Text is { } text:
                writer.WriteStringValue(text);
                return;
            case SourceKind.Blob:
                writer.WriteBase64StringValue(value.Blob);
                return;
            default:
                throw Invalid(column, "the text is not valid in the file's text encoding");
        }
    }

    private static void WriteJson(Utf8JsonWriter writer, string text, string column)
    {
        try
        {
            using var document = JsonDocument.Parse(text, ValueOptions);
            document.RootElement.WriteTo(writer);
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException)
        {
            throw Invalid(column, "the value is not JSON TerraBase can store (at most 16 levels in a document)");
        }
    }

    private static string? Temporal(string text, ColumnMapping mapping)
    {
        var match = TemporalText().Match(text);
        if (!match.Success || mapping == ColumnMapping.Date != (match.Groups["time"].Length == 0)
            || mapping == ColumnMapping.Timestamptz != match.Groups["sign"].Length > 0
            || !DateTime.TryParseExact(match.Groups["date"].Value + " " + (match.Groups["time"].Length > 0 ? match.Groups["time"].Value : "00:00:00"),
                "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture, DateTimeStyles.None, out var local))
        {
            return null;
        }

        var fraction = match.Groups["fraction"].Value;
        if (mapping == ColumnMapping.Date)
        {
            return match.Groups["date"].Value;
        }

        if (mapping == ColumnMapping.Timestamp)
        {
            return match.Groups["date"].Value + "T" + match.Groups["time"].Value + fraction;
        }

        var offset = (int.Parse(match.Groups["hours"].Value, CultureInfo.InvariantCulture) * 3600)
            + ((match.Groups["minutes"].Success ? int.Parse(match.Groups["minutes"].Value, CultureInfo.InvariantCulture) : 0) * 60)
            + (match.Groups["seconds"].Success ? int.Parse(match.Groups["seconds"].Value, CultureInfo.InvariantCulture) : 0);
        var ticks = local.Ticks - ((match.Groups["sign"].Value == "-" ? -offset : offset) * TimeSpan.TicksPerSecond);
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            return null;
        }

        return new DateTime(ticks).ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture) + fraction + "Z";
    }

    private static bool IsHex(ReadOnlySpan<char> text)
    {
        foreach (var c in text)
        {
            if (!char.IsAsciiHexDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static ImportValueException Invalid(string column, string message) => new("invalid_value", message, column);

    [GeneratedRegex(@"\([^)]*\)")]
    private static partial Regex TypeModifiers();

    [GeneratedRegex(@"^-?(0|[1-9][0-9]*)(\.[0-9]+)?([eE][+-]?[0-9]+)?$")]
    private static partial Regex FloatText();

    [GeneratedRegex(@"^(-?[0-9]+(\.[0-9]+)?|NaN|Infinity|-Infinity)$")]
    private static partial Regex DecimalText();

    [GeneratedRegex(@"^(?<date>[0-9]{4}-[0-9]{2}-[0-9]{2})(?: (?<time>[0-9]{2}:[0-9]{2}:[0-9]{2})(?<fraction>\.[0-9]{1,6})?(?:(?<sign>[+-])(?<hours>[0-9]{2})(?::(?<minutes>[0-9]{2}))?(?::(?<seconds>[0-9]{2}))?)?)?$")]
    private static partial Regex TemporalText();

    /// <summary>A pooled buffer with a writer over it.</summary>
    private sealed class PooledWriter : IDisposable
    {
        private readonly ArrayBufferWriter<byte> _buffer = new(64);

        public PooledWriter()
        {
            Writer = new Utf8JsonWriter(_buffer, WriterOptions);
        }

        public Utf8JsonWriter Writer { get; }

        public ReadOnlyMemory<byte> Written => _buffer.WrittenMemory;

        public void Dispose() => Writer.Dispose();
    }
}
