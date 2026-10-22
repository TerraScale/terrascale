using System.Globalization;

namespace TerraBase.Features.Postgres;

/// <summary>The canned <c>pg_catalog</c> answers a driver reads when it connects.</summary>
internal static class PgCatalog
{
    // Built-in base types a driver may name; "unknown" is a pseudo-type Npgsql also loads.
    private static readonly (int Oid, string Name)[] Types =
    [
        (16, "bool"), (17, "bytea"), (20, "int8"), (21, "int2"), (23, "int4"), (25, "text"), (114, "json"), (700, "float4"),
        (701, "float8"), (705, "unknown"), (1043, "varchar"), (1082, "date"), (1114, "timestamp"), (1184, "timestamptz"),
        (1700, "numeric"), (2950, "uuid"), (3802, "jsonb"),
    ];

    /// <summary>
    /// Whether a simple query is Npgsql's type-loading batch (PostgresDatabaseInfo.LoadBackendTypes, Npgsql 10): version,
    /// types, composite fields and enum labels, in one message.
    /// </summary>
    public static bool IsTypeLoad(string sql) =>
        sql.Contains("FROM pg_type AS typ", StringComparison.Ordinal) && sql.Contains("pg_namespace", StringComparison.Ordinal);

    /// <summary>The four results of the type-loading batch: no composite or enum types exist.</summary>
    public static PgResult[] TypeLoad() =>
    [
        PostgresStatements.Version(),
        new(Text("nspname", "oid", "typname", "typtype", "typnotnull", "elemtypoid"),
            [.. Types.Select(type => new string?[] { "pg_catalog", type.Oid.ToString(CultureInfo.InvariantCulture), type.Name, type.Oid == 705 ? "p" : "b", "f", null })],
            $"SELECT {Types.Length}"),
        new(Text("oid", "attname", "atttypid"), [], "SELECT 0"),
        new(Text("oid", "enumlabel"), [], "SELECT 0"),
    ];

    private static PgColumn[] Text(params string[] names) => [.. names.Select(name => new PgColumn(name, PostgresStatements.TextOid))];
}
