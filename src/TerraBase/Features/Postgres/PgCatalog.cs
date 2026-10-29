using System.Globalization;
using System.Text.RegularExpressions;
using SqlParser;
using SqlParser.Dialects;
using SqlParser.Tokens;
using TerraBase.Features.Sql;

namespace TerraBase.Features.Postgres;

/// <summary>
/// Canned <c>pg_catalog</c> answers: Npgsql's type loading, the session functions, and the queries psql 16 and 17 send
/// for <c>\dt</c>, <c>\d</c>, <c>\d table</c>, <c>\l</c> and <c>\dn</c> (src/bin/psql/describe.c), answered from the
/// SQL tables of the database. Every other catalog query is refused.
/// </summary>
internal static partial class PgCatalog
{
    // Npgsql EF provider 10.0.3, NpgsqlDatabaseCreator.CreateHasTablesCommand. Match the complete token
    // sequence: accepting just the relations or a SELECT prefix would silently ignore client predicates.
    private static readonly string[] HasTablesTokens = Tokens("""
        SELECT CASE WHEN COUNT(*) = 0 THEN FALSE ELSE TRUE END
        FROM pg_class AS cls
        JOIN pg_namespace AS ns ON ns.oid = cls.relnamespace
        WHERE
            cls.relkind IN ('r', 'v', 'm', 'f', 'p') AND
            ns.nspname NOT IN ('pg_catalog', 'information_schema') AND
            NOT EXISTS (
                SELECT 1 FROM pg_depend WHERE
                    classid=(
                        SELECT cls.oid
                        FROM pg_class AS cls
                        JOIN pg_namespace AS ns ON ns.oid = cls.relnamespace
                        WHERE relname='pg_class' AND ns.nspname='pg_catalog'
                    ) AND
                    objid=cls.oid AND
                    deptype IN ('e', 'x')
            )
        """);

    /// <summary>
    /// The EF table-existence operation over this session's real SQL catalog. All supported SQL tables are in public
    /// and none belong to extensions. This must run in the SQL session, so uncommitted CREATE/DROP and replay reads
    /// have the same visibility as ordinary SQL. Other pg_class/pg_depend query shapes remain unsupported.
    /// </summary>
    public static string? SessionSql(string sql) => Tokens(sql).SequenceEqual(HasTablesTokens, StringComparer.Ordinal)
        ? "SELECT EXISTS (SELECT 1 FROM sqlite_master WHERE type = 'table')"
        : null;

    /// <summary>The recognised CASE/COUNT query always returns one PostgreSQL boolean, including for an empty schema.</summary>
    public static PgResult HasTablesResult(SqlResult result) => new([new PgColumn("case", 16)], result.Rows, "SELECT 1") { ReadCharge = result.ReadCharge };

    private static string[] Tokens(string sql) => [.. new Tokenizer().Tokenize(sql, new PostgreSqlDialect())
        .Where(token => token is not Whitespace)
        .Select(token => token is Word { QuoteStyle: null } word ? word.Value.ToUpperInvariant() : token.ToString()
            ?? throw new InvalidOperationException("a PostgreSQL token did not render"))];

    // Built-in base types a driver may name; "unknown" is a pseudo-type Npgsql also loads.
    private static readonly (int Oid, string Name)[] Types =
    [
        (16, "bool"), (17, "bytea"), (20, "int8"), (21, "int2"), (23, "int4"), (25, "text"), (114, "json"), (700, "float4"),
        (701, "float8"), (705, "unknown"), (1043, "varchar"), (1082, "date"), (1114, "timestamp"), (1184, "timestamptz"),
        (1700, "numeric"), (2950, "uuid"), (3802, "jsonb"),
    ];

    // \d+ adds storage, compression, statistics target and description.
    private static readonly string[] VerboseNames = ["attstorage", "attcompression", "attstattarget", "description"];
    private static readonly string?[] VerboseValues = ["x", "", null, null];

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

    /// <summary>Whether a statement reads the catalog or only calls session functions.</summary>
    public static bool IsCatalog(string sql, IEnumerable<Token> tokens)
    {
        Token? previous = null;
        foreach (var token in tokens.Where(token => token is not Whitespace))
        {
            if (token is Period && previous is Word qualifier
                && (Identifier(qualifier, "pg_catalog") || Identifier(qualifier, "information_schema")))
            {
                return true;
            }

            if (token is Word table && previous is Word { QuoteStyle: null } clause
                && (Identifier(clause, "FROM") || Identifier(clause, "JOIN"))
                && CatalogRelationPattern().IsMatch(table.QuoteStyle is null ? table.Value.ToLowerInvariant() : table.Value))
            {
                return true;
            }

            previous = token;
        }

        return FunctionsPattern().IsMatch(sql);
    }

    private static bool Identifier(Word word, string value) => word.Value.Equals(value,
        word.QuoteStyle is null ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

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

    /// <summary>Answers a recognised psql query; table OIDs are <see cref="PostgresStatements.FirstTableOid"/> plus the table's position.</summary>
    public static PgOutcome Answer(string sql, string database, string user, IReadOnlyList<SqlTableInfo> tables)
    {
        var q = Spaces().Replace(sql, " ");
        var oid = OidPattern().Match(q) is { Success: true } match ? int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) - PostgresStatements.FirstTableOid : -1;
        var table = oid >= 0 && oid < tables.Count ? tables[oid] : null;
        var listed = tables.Select((entry, index) => (entry.Name, Oid: PostgresStatements.FirstTableOid + index))
            .Where(entry => Visible(q, "relname", entry.Name) && Visible(q, "nspname", "public")).ToList();
        if (q.StartsWith("SELECT c.relchecks, c.relkind,", StringComparison.Ordinal))
        {
            // psql's table header for server 12 and later: a plain heap table without indexes, rules, triggers or policies.
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
            // \d table footers (indexes, constraints, policies, statistics, publications, inheritance): none exist.
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

    // A listing whose columns are `expr AS "Label"`: each label's value, null when unknown.
    private static PgResult[] Aliased(string q, IEnumerable<Dictionary<string, string?>> rows)
    {
        var labels = AliasPattern().Matches(q[..Math.Max(0, q.IndexOf(" FROM ", StringComparison.Ordinal))]).Select(match => match.Groups[1].Value).ToArray();
        IReadOnlyList<SqlValue>[] values = [.. rows.Select(row => Row([.. labels.Select(row.GetValueOrDefault)]))];
        return [new(Text(labels), values, $"SELECT {values.Length}")];
    }

    // Whether every psql name pattern on `column` (`x.column OPERATOR(pg_catalog.~) '^(...)$'`) matches `value`.
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

    // ListTablesAsync reports no DEFAULT, so psql's Default column stays empty.
    private static string TypeName(string? declared) => PostgresStatements.Oid(declared) switch
    {
        20 => "bigint",
        21 => "smallint",
        23 => "integer",
        701 => "double precision",
        1700 => "numeric",
        25 => "text",
        1043 => "character varying",
        16 => "boolean",
        114 => "json",
        3802 => "jsonb",
        1082 => "date",
        1114 => "timestamp without time zone",
        1184 => "timestamp with time zone",
        _ => string.IsNullOrEmpty(declared) ? "text" : declared.ToLowerInvariant(),
    };

    private static IReadOnlyList<SqlValue> Row(params string?[] values) => [.. values.Select(value => value is null ? SqlValue.Null : SqlValue.Of(value))];

    private static PgColumn[] Text(params string[] names) => [.. names.Select(name => new PgColumn(name, PostgresStatements.TextOid))];

    // Preserve the known catalog relation set: pg_orders and other user pg_* tables are ordinary SQL.
    [GeneratedRegex(@"\A(?:pg_(?:aggregate|am|amop|amproc|attrdef|attribute|auth_members|authid|available_extensions|cast|class|collation|constraint|conversion|database|db_role_setting|default_acl|depend|description|enum|event_trigger|extension|foreign_data_wrapper|foreign_server|foreign_table|group|index|indexes|inherits|language|locks|matviews|namespace|opclass|operator|opfamily|policies|policy|prepared_statements|proc|publication|range|replication_slots|rewrite|roles|rules|sequence|sequences|settings|shadow|shdepend|shdescription|statistic|statistic_ext|tables|tablespace|timezone_names|trigger|ts_config|ts_dict|type|user|user_mappings|views)|pg_stat(?:io)?_\w+)\z", RegexOptions.CultureInvariant)]
    private static partial Regex CatalogRelationPattern();

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
