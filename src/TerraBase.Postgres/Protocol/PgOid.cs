using System.Collections.Frozen;
using System.Diagnostics.CodeAnalysis;

namespace TerraBase.Postgres.Protocol;

internal sealed record PgType(int Oid, string Name, string FormatName, short Length, char Category, int ArrayOid, string[] DeclaredNames);

internal static class PgOid
{
    public const int Bool = 16;
    public const int Bytea = 17;
    public const int Int8 = 20;
    public const int Int2 = 21;
    public const int Int4 = 23;
    public const int Text = 25;
    public const int Oid = 26;
    public const int Json = 114;
    public const int Float4 = 700;
    public const int Float8 = 701;
    public const int Unknown = 705;
    public const int Bpchar = 1042;
    public const int Varchar = 1043;
    public const int Date = 1082;
    public const int Time = 1083;
    public const int Timestamp = 1114;
    public const int Timestamptz = 1184;
    public const int Numeric = 1700;
    public const int Uuid = 2950;
    public const int Jsonb = 3802;

    public static readonly PgType[] Types =
    [
        new(Bool, "bool", "boolean", 1, 'B', 1000, ["BOOLEAN", "BOOL"]),
        new(Bytea, "bytea", "bytea", -1, 'U', 1001, ["BYTEA", "BLOB"]),
        new(Int8, "int8", "bigint", 8, 'N', 1016, ["BIGINT", "INT8", "INTEGER", "BIGSERIAL", "SERIAL8", "SERIAL", "SERIAL4"]),
        new(Int2, "int2", "smallint", 2, 'N', 1005, ["SMALLINT", "INT2", "SMALLSERIAL", "SERIAL2", "TINYINT"]),
        new(Int4, "int4", "integer", 4, 'N', 1007, ["INT", "INT4", "MEDIUMINT"]),
        new(Text, "text", "text", -1, 'S', 1009, ["TEXT", "CLOB", "CITEXT", "NAME"]),
        new(Oid, "oid", "oid", 4, 'N', 1028, ["OID"]),
        new(Json, "json", "json", -1, 'U', 199, ["JSON"]),
        new(Float4, "float4", "real", 4, 'N', 1021, ["REAL", "FLOAT4"]),
        new(Float8, "float8", "double precision", 8, 'N', 1022, ["DOUBLE PRECISION", "DOUBLE", "FLOAT8", "FLOAT"]),
        new(Unknown, "unknown", "unknown", -2, 'X', 0, []),
        new(Bpchar, "bpchar", "character", -1, 'S', 1014, ["CHAR", "CHARACTER", "BPCHAR", "NCHAR"]),
        new(Varchar, "varchar", "character varying", -1, 'S', 1015, ["VARCHAR", "CHARACTER VARYING", "NVARCHAR", "VARYING CHARACTER"]),
        new(Date, "date", "date", 4, 'D', 1182, ["DATE"]),
        new(Time, "time", "time without time zone", 8, 'D', 1183, ["TIME", "TIME WITHOUT TIME ZONE"]),
        new(Timestamp, "timestamp", "timestamp without time zone", 8, 'D', 1115, ["TIMESTAMP", "TIMESTAMP WITHOUT TIME ZONE", "DATETIME"]),
        new(Timestamptz, "timestamptz", "timestamp with time zone", 8, 'D', 1185, ["TIMESTAMPTZ", "TIMESTAMP WITH TIME ZONE"]),
        new(Numeric, "numeric", "numeric", -1, 'N', 1231, ["NUMERIC", "DECIMAL", "MONEY"]),
        new(Uuid, "uuid", "uuid", 16, 'U', 2951, ["UUID"]),
        new(Jsonb, "jsonb", "jsonb", -1, 'U', 3807, ["JSONB"]),
    ];

    private static readonly FrozenDictionary<string, int> ByDeclaredName = Types
        .SelectMany(static type => type.DeclaredNames.Select(name => KeyValuePair.Create(name, type.Oid)))
        .ToFrozenDictionary(StringComparer.Ordinal);

    private static readonly FrozenDictionary<int, PgType> ByOid = Types.ToFrozenDictionary(static type => type.Oid);

    private static readonly FrozenDictionary<int, int> ElementByArray = Types
        .Where(static type => type.ArrayOid != 0)
        .ToFrozenDictionary(static type => type.ArrayOid, static type => type.Oid);

    public static IEnumerable<KeyValuePair<string, int>> DeclaredNames => ByDeclaredName;

    public static bool IsKnown(int oid) => ByOid.ContainsKey(oid);

    public static bool TryGet(int oid, [MaybeNullWhen(false)] out PgType type) => ByOid.TryGetValue(oid, out type);

    public static bool TryGetArrayElement(int arrayOid, out int elementOid) => ElementByArray.TryGetValue(arrayOid, out elementOid);

    public static string FormatName(int oid) => ByOid.TryGetValue(oid, out var type) ? type.FormatName : "text";

    public static int FromDeclaredType(string? declared)
    {
        if (string.IsNullOrWhiteSpace(declared))
        {
            return 0;
        }

        if (ByDeclaredName.TryGetValue(declared, out var direct))
        {
            return direct;
        }

        Span<char> stackBuffer = stackalloc char[64];
        var destination = declared.Length <= stackBuffer.Length ? stackBuffer : new char[declared.Length];
        var pos = 0;
        var depth = 0;
        var space = false;
        foreach (var character in declared)
        {
            if (character == '(')
            {
                depth++;
                continue;
            }

            if (character == ')')
            {
                depth = Math.Max(0, depth - 1);
                continue;
            }

            if (depth > 0)
            {
                continue;
            }

            if (char.IsWhiteSpace(character))
            {
                space = pos > 0;
                continue;
            }

            if (space)
            {
                destination[pos++] = ' ';
                space = false;
            }

            destination[pos++] = char.ToUpperInvariant(character);
        }

        return ByDeclaredName.GetAlternateLookup<ReadOnlySpan<char>>().TryGetValue(destination[..pos], out var oid) ? oid : 0;
    }
}
