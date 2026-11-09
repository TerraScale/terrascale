using System.Collections.Frozen;

namespace TerraBase.Features.Postgres;

internal static class PgOid
{
    public const int Bool = 16;
    public const int Bytea = 17;
    public const int Int8 = 20;
    public const int Int2 = 21;
    public const int Int4 = 23;
    public const int Text = 25;
    public const int Json = 114;
    public const int Float4 = 700;
    public const int Float8 = 701;
    public const int Unknown = 705;
    public const int Bpchar = 1042;
    public const int Varchar = 1043;
    public const int Date = 1082;
    public const int Timestamp = 1114;
    public const int Timestamptz = 1184;
    public const int Uuid = 2950;
    public const int Jsonb = 3802;
    public const int Numeric = 1700;

    internal readonly record struct PgType(int Oid, string CatalogName, string? Name, string[] Aliases, bool Binary);

    internal static readonly PgType[] Types =
    [
        new(Bool, "bool", "boolean", ["BOOLEAN", "BOOL"], true),
        new(Bytea, "bytea", "bytea", ["BLOB", "BYTEA"], true),
        new(Int8, "int8", "bigint", ["BIGINT", "INT8"], true),
        new(Int2, "int2", "smallint", ["SMALLINT", "INT2"], true),
        new(Int4, "int4", "integer", ["INTEGER", "INT", "INT4"], true),
        new(Text, "text", "text", ["TEXT"], true),
        new(Json, "json", "json", ["JSON"], true),
        new(Float4, "float4", null, [], false),
        new(Float8, "float8", "double precision", ["REAL", "FLOAT", "FLOAT4", "FLOAT8", "DOUBLE", "DOUBLE PRECISION"], true),
        new(Unknown, "unknown", null, [], false),
        new(Varchar, "varchar", "character varying", ["VARCHAR", "CHAR", "CHARACTER", "CHARACTER VARYING"], true),
        new(Date, "date", "date", ["DATE"], true),
        new(Timestamp, "timestamp", "timestamp without time zone", ["TIMESTAMP", "TIMESTAMP WITHOUT TIME ZONE"], true),
        new(Timestamptz, "timestamptz", "timestamp with time zone", ["TIMESTAMPTZ", "TIMESTAMP WITH TIME ZONE"], true),
        new(Numeric, "numeric", "numeric", ["NUMERIC", "DECIMAL"], true),
        new(Uuid, "uuid", "uuid", ["UUID"], true),
        new(Jsonb, "jsonb", "jsonb", ["JSONB"], true),
    ];

    private static readonly FrozenDictionary<string, int> ByAlias =
        Types.SelectMany(type => type.Aliases.Select(alias => (alias, type.Oid))).ToFrozenDictionary(entry => entry.alias, entry => entry.Oid, StringComparer.Ordinal);

    private static readonly FrozenDictionary<int, string?> Names = Types.ToFrozenDictionary(type => type.Oid, type => type.Name);

    private static readonly FrozenSet<int> BinaryOids = [.. Types.Where(type => type.Binary).Select(type => type.Oid)];

    public static int Of(string? declared) => declared is null ? 0 : ByAlias.GetValueOrDefault(declared.Split('(')[0].Trim().ToUpperInvariant());

    public static string? Name(int oid) => Names.GetValueOrDefault(oid);

    public static bool Binary(int oid) => BinaryOids.Contains(oid);
}
