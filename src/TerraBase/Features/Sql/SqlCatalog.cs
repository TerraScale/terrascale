using System.Globalization;
using System.Text;
using SqlParser;
using SqlParser.Ast;
using TerraBase.Engine;

namespace TerraBase.Features.Sql;

/// <summary>
/// SQLite's catalog, read-only and derived from the SQL tables' definitions, secondary indexes and unique keys:
/// <c>sqlite_master</c> (and <c>sqlite_schema</c>, empty <c>sqlite_temp_master</c>) and the table-valued pragmas
/// <c>pragma_table_info</c>, <c>pragma_table_xinfo</c>, <c>pragma_index_list</c>, <c>pragma_index_info</c> and
/// <c>pragma_foreign_key_list</c>, whose hidden <c>arg</c> column is the table or index they describe. A read naming them
/// gets them as CTEs of literal rows; PRAGMA statements answer the same rows.
/// </summary>
internal static class SqlCatalog
{
    private static readonly string[] Master = ["type", "name", "tbl_name", "rootpage", "sql"];
    private static readonly string[] TableInfo = ["cid", "name", "type", "notnull", "dflt_value", "pk"];
    private static readonly string[] TableXinfo = [.. TableInfo, "hidden"];
    private static readonly string[] IndexList = ["seq", "name", "unique", "origin", "partial"];
    private static readonly string[] IndexInfo = ["seqno", "cid", "name"];
    private static readonly string[] ForeignKeyList = ["id", "seq", "table", "from", "to", "on_update", "on_delete", "match"];

    /// <summary>Catalog relations by name, with their columns (the pragmas' hidden <c>arg</c> last).</summary>
    public static IReadOnlyDictionary<string, string[]> Relations { get; } = new Dictionary<string, string[]>(StringComparer.Ordinal)
    {
        ["sqlite_master"] = Master,
        ["sqlite_schema"] = Master,
        ["sqlite_temp_master"] = Master,
        ["sqlite_temp_schema"] = Master,
        ["pragma_table_info"] = [.. TableInfo, "arg"],
        ["pragma_table_xinfo"] = [.. TableXinfo, "arg"],
        ["pragma_index_list"] = [.. IndexList, "arg"],
        ["pragma_index_info"] = [.. IndexInfo, "arg"],
        ["pragma_foreign_key_list"] = [.. ForeignKeyList, "arg"],
    };

    /// <summary>
    /// The name a read uses for catalog relation <paramref name="relation"/>: no SQL table can have it (a leading <c>_</c> is
    /// reserved), and no table of the database file has it, so an unresolved reference fails instead of reaching SQLite's
    /// own schema table or pragma.
    /// </summary>
    public static string CteName(string relation) => "_tb_catalog_" + relation;

    /// <summary>The pragmas a FROM item calls as table-valued functions.</summary>
    public static bool IsPragmaFunction(string name) => name.StartsWith("pragma_", StringComparison.Ordinal) && Relations.ContainsKey(name);

    /// <summary>The columns of a PRAGMA statement's result.</summary>
    public static string[] Columns(PragmaKind kind) => kind switch
    {
        PragmaKind.TableXinfo => TableXinfo,
        PragmaKind.IndexList => IndexList,
        PragmaKind.IndexInfo => IndexInfo,
        PragmaKind.ForeignKeyList => ForeignKeyList,
        _ => TableInfo,
    };

    /// <summary>A PRAGMA statement's rows: its relation's rows for its argument, without <c>arg</c>.</summary>
    public static async ValueTask<List<IReadOnlyList<SqlValue>>> PragmaAsync(
        IStorageTransaction transaction, PragmaKind kind, string arg, Func<string, ValueTask<SqlTableDefinition?>> table, CancellationToken cancellationToken)
    {
        var relation = kind switch
        {
            PragmaKind.TableXinfo => "pragma_table_xinfo",
            PragmaKind.IndexList => "pragma_index_list",
            PragmaKind.IndexInfo => "pragma_index_info",
            PragmaKind.ForeignKeyList => "pragma_foreign_key_list",
            _ => "pragma_table_info",
        };

        // An index's table is unknown up front: index_info reads every table.
        var tables = await TablesAsync(transaction, kind == PragmaKind.IndexInfo ? null : arg, table, cancellationToken).ConfigureAwait(false);
        return [.. Rows(relation, tables).Where(row => row[^1].Text == arg).Select(row => (IReadOnlyList<SqlValue>)[.. row.Take(row.Count - 1)])];
    }

    /// <summary>The CTEs that define <paramref name="relations"/> for one read.</summary>
    public static async ValueTask<IReadOnlyList<CommonTableExpression>> CtesAsync(
        IStorageTransaction transaction, IReadOnlyList<string> relations, Func<string, ValueTask<SqlTableDefinition?>> table, CancellationToken cancellationToken)
    {
        var tables = await TablesAsync(transaction, null, table, cancellationToken).ConfigureAwait(false);
        var text = new StringBuilder("WITH ");
        for (var i = 0; i < relations.Count; i++)
        {
            var columns = Relations[relations[i]];
            text.Append(i == 0 ? "" : ", ").Append(CteName(relations[i])).Append('(').AppendJoin(", ", columns.Select(column => '"' + column + '"')).Append(") AS (");
            var rows = Rows(relations[i], tables);
            if (rows.Count == 0)
            {
                text.Append("SELECT ").AppendJoin(", ", columns.Select(_ => "NULL")).Append(" WHERE 0");
            }
            else
            {
                text.Append("VALUES ").AppendJoin(", ", rows.Select(row => "(" + string.Join(", ", row.Select(Literal)) + ")"));
            }

            text.Append(')');
        }

        text.Append(" SELECT 1");
        var query = new Parser().ParseSql(text.ToString(), new SqlDialect()) is [Statement.Select { Query: var parsed }]
            ? parsed
            : throw new InvalidOperationException("the catalog does not parse");
        SqlGuard.Normalize(query);
        return query.With!.CteTables;
    }

    private sealed record IndexEntry(string Name, bool Unique, string Origin, IReadOnlyList<string> Columns, string? Sql);

    private sealed record TableEntry(string Name, SqlTableDefinition Table, IReadOnlyList<IndexEntry> Indexes);

    // Every SQL table (or only `only`) with its indexes, in name order.
    private static async ValueTask<List<TableEntry>> TablesAsync(
        IStorageTransaction transaction, string? only, Func<string, ValueTask<SqlTableDefinition?>> table, CancellationToken cancellationToken)
    {
        var names = new List<string>();
        if (only is not null)
        {
            names.Add(only);
        }
        else
        {
            string? after = null;
            while (true)
            {
                var (page, more) = await transaction.ListCollectionsAsync(after, 1000, cancellationToken).ConfigureAwait(false);
                names.AddRange(page.Select(collection => collection.Name));
                if (!more || page.Count == 0)
                {
                    break;
                }

                after = page[^1].Name;
            }
        }

        var indexes = await transaction.ListIndexesAsync(cancellationToken).ConfigureAwait(false);
        var entries = new List<TableEntry>();
        foreach (var name in names)
        {
            if (await table(name).ConfigureAwait(false) is not { } definition)
            {
                continue;
            }

            var unique = await transaction.GetUniqueConstraintsAsync(name, cancellationToken).ConfigureAwait(false);
            var secondary = indexes.Where(index => index.Collection == name).ToList();
            var list = new List<IndexEntry>();
            // SQLite numbers the automatic indexes of a table's PRIMARY KEY and UNIQUE constraints sqlite_autoindex_<t>_<n>;
            // an INTEGER PRIMARY KEY is the rowid and has none.
            if (definition.PrimaryKey.Count > 0 && !(definition.PrimaryKey is [var key] && definition.Columns.First(column => column.Name == key) is var column
                && (column.Affinity == SqlAffinity.Integer || SqlTable.IsSerial(column.Type))))
            {
                list.Add(new IndexEntry(Auto(name, list.Count), true, "pk", definition.PrimaryKey, null));
            }

            foreach (var constraint in unique.Where(constraint => secondary.All(index => index.Name != constraint.Name)))
            {
                list.Add(new IndexEntry(Auto(name, list.Count), true, "u", constraint.Fields, null));
            }

            foreach (var index in secondary)
            {
                var isUnique = unique.Any(constraint => constraint.Name == index.Name);
                var columns = index.Fields.Select(field => field.Path).ToList();
                list.Add(new IndexEntry(index.Name, isUnique, "c", columns,
                    $"CREATE {(isUnique ? "UNIQUE " : "")}INDEX {index.Name} ON {name} ({string.Join(", ", columns)})"));
            }

            entries.Add(new TableEntry(name, definition, list));
        }

        return entries;
    }

    private static string Auto(string table, int count) => string.Create(CultureInfo.InvariantCulture, $"sqlite_autoindex_{table}_{count + 1}");

    // A relation's rows, each ending with `arg` for the pragmas.
    private static List<List<SqlValue>> Rows(string relation, List<TableEntry> tables)
    {
        var rows = new List<List<SqlValue>>();
        foreach (var entry in tables)
        {
            var table = entry.Table;
            switch (relation)
            {
                case "sqlite_master" or "sqlite_schema":
                    rows.Add([SqlValue.Of("table"), SqlValue.Of(entry.Name), SqlValue.Of(entry.Name), SqlValue.Of(0), SqlValue.Of(table.Sql ?? Create(entry.Name, table))]);
                    rows.AddRange(entry.Indexes.Select(index => new List<SqlValue>
                    {
                        SqlValue.Of("index"), SqlValue.Of(index.Name), SqlValue.Of(entry.Name), SqlValue.Of(0), index.Sql is { } sql ? SqlValue.Of(sql) : SqlValue.Null,
                    }));
                    break;
                case "pragma_table_info" or "pragma_table_xinfo":
                    for (var i = 0; i < table.Columns.Count; i++)
                    {
                        var column = table.Columns[i];
                        List<SqlValue> row =
                        [
                            SqlValue.Of(i), SqlValue.Of(column.Name), SqlValue.Of(column.Type), SqlValue.Of(column.NotNull ? 1 : 0), Default(column),
                            SqlValue.Of(table.PrimaryKey.ToList().IndexOf(column.Name) + 1),
                        ];
                        if (relation == "pragma_table_xinfo")
                        {
                            row.Add(SqlValue.Of(0));
                        }

                        row.Add(SqlValue.Of(entry.Name));
                        rows.Add(row);
                    }

                    break;
                case "pragma_index_list":
                    rows.AddRange(entry.Indexes.Select((index, seq) => new List<SqlValue>
                    {
                        SqlValue.Of(seq), SqlValue.Of(index.Name), SqlValue.Of(index.Unique ? 1 : 0), SqlValue.Of(index.Origin), SqlValue.Of(0), SqlValue.Of(entry.Name),
                    }));
                    break;
                case "pragma_index_info":
                    foreach (var index in entry.Indexes)
                    {
                        rows.AddRange(index.Columns.Select((name, seq) => new List<SqlValue>
                        {
                            SqlValue.Of(seq), SqlValue.Of(table.Columns.ToList().FindIndex(column => column.Name == name)), SqlValue.Of(name), SqlValue.Of(index.Name),
                        }));
                    }

                    break;
                case "pragma_foreign_key_list":
                    var keys = table.ForeignKeys ?? [];
                    for (var id = 0; id < keys.Count; id++)
                    {
                        var key = keys[id];
                        for (var seq = 0; seq < key.From.Count; seq++)
                        {
                            rows.Add(
                            [
                                SqlValue.Of(id), SqlValue.Of(seq), SqlValue.Of(key.Table), SqlValue.Of(key.From[seq]), seq < key.To.Count ? SqlValue.Of(key.To[seq]) : SqlValue.Null,
                                SqlValue.Of(key.OnUpdate), SqlValue.Of(key.OnDelete), SqlValue.Of("NONE"), SqlValue.Of(entry.Name),
                            ]);
                        }
                    }

                    break;
            }
        }

        return rows;
    }

    // A DEFAULT as SQL text, as table_info shows it.
    private static SqlValue Default(SqlColumnDefinition column) =>
        column.Default is { } fallback && SqlWrite.Default(fallback) is var value
            ? value.Kind == SqlValueKind.Text ? SqlValue.Of("'" + value.Text!.Replace("'", "''", StringComparison.Ordinal) + "'") : SqlValue.Of(fallback.GetRawText())
            : SqlValue.Null;

    // The statement of a table defined before its text was kept.
    private static string Create(string name, SqlTableDefinition table)
    {
        var columns = table.Columns.Select(column => $"{column.Name}{(column.Type.Length > 0 ? " " + column.Type : "")}{(column.NotNull ? " NOT NULL" : "")}");
        var key = table.PrimaryKey.Count > 0 ? $", PRIMARY KEY ({string.Join(", ", table.PrimaryKey)})" : "";
        return $"CREATE TABLE {name} ({string.Join(", ", columns)}{key})";
    }

    // A value as an SQL literal; NUL cannot appear in a '…' literal, so it is joined in with char(0).
    private static string Literal(SqlValue value) => value.Kind switch
    {
        SqlValueKind.Integer => value.Integer.ToString(CultureInfo.InvariantCulture),
        SqlValueKind.Text => "'" + value.Text!.Replace("'", "''", StringComparison.Ordinal).Replace("\0", "' || char(0) || '", StringComparison.Ordinal) + "'",
        _ => "NULL",
    };
}
