using System.Globalization;
using System.Text.RegularExpressions;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Postgres;

internal static partial class PgCatalog
{
    private static readonly (int Oid, string Name)[] Types = [.. PgOid.Types.Select(type => (type.Oid, type.CatalogName))];

    private static readonly string[] VerboseNames = ["attstorage", "attcompression", "attstattarget", "description"];
    private static readonly string?[] VerboseValues = ["x", "", null, null];

    public static bool IsTypeLoad(string sql) =>
        sql.Contains("FROM pg_type AS typ", StringComparison.Ordinal) && sql.Contains("pg_namespace", StringComparison.Ordinal);

    public static PgResult[] TypeLoad() =>
    [
        new(Text("version"), [Row(PostgresStatements.Version)], "SELECT 1"),
        new(Text("nspname", "oid", "typname", "typtype", "typnotnull", "elemtypoid"),
            [.. Types.Select(type => Row("pg_catalog", type.Oid.ToString(CultureInfo.InvariantCulture), type.Name, type.Oid == PgOid.Unknown ? "p" : "b", "f", null))],
            $"SELECT {Types.Length}"),
        new(Text("oid", "attname", "atttypid"), [], "SELECT 0"),
        new(Text("oid", "enumlabel"), [], "SELECT 0"),
    ];

    public static bool IsCatalog(string sql) => CatalogPattern().IsMatch(sql) || FunctionsPattern().IsMatch(sql);

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

    public static PgOutcome Answer(string sql, string database, string user, IReadOnlyList<SqlTableInfo> tables)
    {
        var q = Spaces().Replace(sql, " ");
        var oid = OidPattern().Match(q) is { Success: true } match ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - PostgresStatements.FirstTableOid : -1;
        var table = oid >= 0 && oid < tables.Count ? tables[oid] : null;
        var listed = tables.Select((entry, index) => (entry.Name, Oid: PostgresStatements.FirstTableOid + index))
            .Where(entry => Visible(q, "relname", entry.Name) && Visible(q, "nspname", "public")).ToList();
        if (q.StartsWith("SELECT c.relchecks, c.relkind,", StringComparison.Ordinal))
        {
            return One(new(Text("relchecks", "relkind", "relhasindex", "relhasrules", "relhastriggers", "relrowsecurity", "relforcerowsecurity", "relhasoids",
                "relispartition", "reloptions", "reltablespace", "reloftype", "relpersistence", "relreplident", "amname"),
                table is null ? [] : [Row("0", "r", "f", "f", "f", "f", "f", "f", "f", "", "0", "", "p", "d", "heap")], table is null ? "SELECT 0" : "SELECT 1"));
        }

        if (q.StartsWith("SELECT a.attname,", StringComparison.Ordinal) && q.Contains("FROM pg_catalog.pg_attribute a", StringComparison.Ordinal))
        {
            var verbose = q.Contains("a.attstorage", StringComparison.Ordinal);
            var columns = table?.Columns.Select(column => Row([column.Name, TypeName(column.DeclType), null, column.NotNull ? "t" : "f", null, "", "",
                .. verbose ? VerboseValues : []])).ToList() ?? [];
            return One(new(Text(["attname", "format_type", "default", "attnotnull", "attcollation", "attidentity", "attgenerated",
                .. verbose ? VerboseNames : []]), [.. columns], $"SELECT {columns.Count}"));
        }

        if (q.StartsWith("SELECT c.oid, n.nspname, c.relname FROM pg_catalog.pg_class c", StringComparison.Ordinal))
        {
            return One(new(Text("oid", "nspname", "relname"),
                [.. listed.Select(entry => Row(entry.Oid.ToString(CultureInfo.InvariantCulture), "public", entry.Name))], $"SELECT {listed.Count}"));
        }

        if (q.Contains("FROM pg_catalog.pg_class c", StringComparison.Ordinal) && q.Contains("c.relkind IN ('r'", StringComparison.Ordinal))
        {
            return Aliased(q, listed.Select(entry => new Dictionary<string, string?>
            {
                ["Schema"] = "public",
                ["Name"] = entry.Name,
                ["Type"] = "table",
                ["Owner"] = user,
                ["Persistence"] = "permanent",
                ["Access method"] = "heap",
            }));
        }

        if (FooterPattern().IsMatch(q))
        {
            return One(new([], [], "SELECT 0"));
        }

        if (q.Contains("FROM pg_catalog.pg_database d", StringComparison.Ordinal))
        {
            return Aliased(q, Visible(q, "datname", database)
                ? [new() { ["Name"] = database, ["Owner"] = user, ["Encoding"] = "UTF8", ["Locale Provider"] = "libc", ["Collate"] = "C", ["Ctype"] = "C" }]
                : []);
        }

        if (q.Contains("FROM pg_catalog.pg_namespace n", StringComparison.Ordinal))
        {
            return Aliased(q, Visible(q, "nspname", "public") ? [new() { ["Name"] = "public", ["Owner"] = user }] : []);
        }

        return new PgError("0A000", "pg_catalog query not supported: TerraBase answers psql's \\d, \\dt, \\l and \\dn");
    }

    private static PgResult[] One(PgResult result) => [result];

    private static PgResult[] Aliased(string q, IEnumerable<Dictionary<string, string?>> rows)
    {
        var labels = AliasPattern().Matches(q[..Math.Max(0, q.IndexOf(" FROM ", StringComparison.Ordinal))]).Select(match => match.Groups[1].Value).ToArray();
        IReadOnlyList<SqlValue>[] values = [.. rows.Select(row => Row([.. labels.Select(row.GetValueOrDefault)]))];
        return [new(Text(labels), values, $"SELECT {values.Length}")];
    }

    private static bool Visible(string q, string column, string value) => NamePattern().Matches(q)
        .Where(match => match.Groups[1].Value == column)
        .All(match =>
        {
            try
            {
                return Regex.IsMatch(value, match.Groups[2].Value.Replace("''", "'", StringComparison.Ordinal), RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
            }
            catch (ArgumentException)
            {
                return false;
            }
        });

    private static string TypeName(string? declared) => PgOid.Name(PgOid.Of(declared)) is { } named
        ? named
        : string.IsNullOrEmpty(declared) ? "text" : declared.ToLowerInvariant();

    private static IReadOnlyList<SqlValue> Row(params string?[] values) => [.. values.Select(value => value is null ? SqlValue.Null : SqlValue.Of(value))];

    private static PgColumn[] Text(params string[] names) => [.. names.Select(name => new PgColumn(name, PgOid.Text))];

    [GeneratedRegex(@"\b(?:pg_catalog|information_schema)\s*\.|\b(?:FROM|JOIN)\s+(?:pg_(?:aggregate|am|amop|amproc|attrdef|attribute|auth_members|authid|available_extensions|cast|class|collation|constraint|conversion|database|db_role_setting|default_acl|depend|description|enum|event_trigger|extension|foreign_data_wrapper|foreign_server|foreign_table|group|index|indexes|inherits|language|locks|matviews|namespace|opclass|operator|opfamily|policies|policy|prepared_statements|proc|publication|range|replication_slots|rewrite|roles|rules|sequence|sequences|settings|shadow|shdepend|shdescription|statistic|statistic_ext|tables|tablespace|timezone_names|trigger|ts_config|ts_dict|type|user|user_mappings|views)|pg_stat(?:io)?_\w+)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CatalogPattern();

    [GeneratedRegex(@"\ASELECT\s+((?:(?:pg_catalog\.)?(?:current_database\(\)|current_schema(?:\(\))?|current_user|session_user|version\(\))\s*(?:,\s*|\z))+)\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FunctionsPattern();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Spaces();

    [GeneratedRegex(@"\.(?:oid|attrelid) = '(\d{1,9})'", RegexOptions.CultureInvariant)]
    private static partial Regex OidPattern();

    [GeneratedRegex(@"\b\w+\.(relname|nspname|datname) OPERATOR\(pg_catalog\.~\) '((?:[^']|'')*)'", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    [GeneratedRegex(@"\bas ""([^""]+)""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AliasPattern();

    [GeneratedRegex(@"pg_catalog\.(?:pg_policy|pg_statistic_ext|pg_publication|pg_inherits|pg_trigger|pg_constraint|pg_index|pg_rewrite)\b", RegexOptions.CultureInvariant)]
    private static partial Regex FooterPattern();
}
