using System.Globalization;
using System.Text.RegularExpressions;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Postgres;

/// <summary>
/// Canned <c>pg_catalog</c> answers: Npgsql's type loading and the session functions.
/// </summary>
internal static partial class PgCatalog
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
        new(Text("version"), [Row(PostgresStatements.Version)], "SELECT 1"),
        new(Text("nspname", "oid", "typname", "typtype", "typnotnull", "elemtypoid"),
            [.. Types.Select(type => Row("pg_catalog", type.Oid.ToString(CultureInfo.InvariantCulture), type.Name, type.Oid == 705 ? "p" : "b", "f", null))],
            $"SELECT {Types.Length}"),
        new(Text("oid", "attname", "atttypid"), [], "SELECT 0"),
        new(Text("oid", "enumlabel"), [], "SELECT 0"),
    ];

    /// <summary>Whether a statement only calls session functions.</summary>
    public static bool IsCatalog(string sql) => FunctionsPattern().IsMatch(sql);

    /// <summary><c>SELECT current_database(), current_schema, current_user, version()</c> in any combination; null otherwise.</summary>
    public static PgResult? Functions(string sql, string database, string user)
    {
        var match = FunctionsPattern().Match(sql);
        if (!match.Success)
        {
            return null;
        }

        var names = match.Groups[1].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(name => name.Replace("pg_catalog.", "", StringComparison.Ordinal).Replace("()", "", StringComparison.Ordinal)).ToArray();
        return new(Text(names), [Row([.. names.Select(name => name switch
        {
            "current_database" => database,
            "current_schema" => "public",
            "version" => PostgresStatements.Version,
            _ => user,
        })])], "SELECT 1");
    }

    private static IReadOnlyList<SqlValue> Row(params string?[] values) => [.. values.Select(value => value is null ? SqlValue.Null : SqlValue.Of(value))];

    private static PgColumn[] Text(params string[] names) => [.. names.Select(name => new PgColumn(name, PostgresStatements.TextOid))];

    [GeneratedRegex(@"\ASELECT\s+((?:(?:pg_catalog\.)?(?:current_database\(\)|current_schema(?:\(\))?|current_user|session_user|version\(\))\s*(?:,\s*|\z))+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FunctionsPattern();

}
