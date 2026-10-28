using System.Collections.Concurrent;

namespace TerraBase.Features.Sql;

/// <summary>
/// A database's repeated reads (docs/terrabase/sql/read-cache.md): the same SELECT text skips tokenizing, parsing and
/// validation, and over the same table definitions its compilation (<see cref="SqlRead.Compile"/>) too. Its arguments are
/// still bound per statement. Bounded by entries and estimated bytes: an insert past either bound starts over empty.
/// </summary>
internal sealed class SqlReadCache
{
    private const int MaxEntries = 4096;
    private const long MaxBytes = 64L * 1024 * 1024;

    // A parsed tree is estimated at this many bytes per character of its text.
    private const int TreeBytesPerChar = 64;

    private readonly ConcurrentDictionary<(string Database, string Sql), ReadSql> _reads = new();
    private readonly ConcurrentDictionary<TemplateKey, SqlReadTemplate> _templates = new();
    private readonly Lock _gate = new();
    private int _entries;
    private long _bytes;

    private readonly record struct TemplateKey(string Database, string Sql, string Tables, bool Describe, string? Keyed);

    /// <summary>
    /// <see cref="SqlParse.Parse"/> of <paramref name="sql"/>; a read comes back shared (<see cref="ReadSql.Shared"/>), and the
    /// same text in the same database gets the same one.
    /// </summary>
    public SqlParseResult Parse(string? database, string sql)
    {
        if (database is null)
        {
            return SqlParse.Parse(sql);
        }

        if (_reads.TryGetValue((database, sql), out var known))
        {
            return known;
        }

        var parsed = SqlParse.Parse(sql);
        if (parsed is not ReadSql read)
        {
            return parsed;
        }

        var shared = read with { Shared = sql };
        Add(_reads, (database, sql), shared, (long)TreeBytesPerChar * sql.Length);
        return shared;
    }

    /// <summary>The template of a shared read over tables whose definitions serialize to <paramref name="tables"/>, if compiled.</summary>
    public SqlReadTemplate? Template(string database, string sql, string tables, bool describe, string? keyed) =>
        _templates.GetValueOrDefault(new TemplateKey(database, sql, tables, describe, keyed));

    public void Add(string database, string sql, string tables, bool describe, string? keyed, SqlReadTemplate template)
    {
        var bytes = 2L * (sql.Length + tables.Length + template.Statement.Length + (template.Ctes?.Length ?? 0))
            + template.Tables.Sum(table => 64L + table.Columns.Sum(column => 2L * column.Length + 32)) + 1024;
        Add(_templates, new TemplateKey(database, sql, tables, describe, keyed), template, bytes);
    }

    private void Add<TKey, TValue>(ConcurrentDictionary<TKey, TValue> entries, TKey key, TValue value, long bytes)
        where TKey : notnull
    {
        // One entry never takes more than a sixteenth of the cache.
        if (bytes > MaxBytes / 16)
        {
            return;
        }

        lock (_gate)
        {
            if (_entries >= MaxEntries || _bytes + bytes > MaxBytes)
            {
                _reads.Clear();
                _templates.Clear();
                _entries = 0;
                _bytes = 0;
            }

            if (entries.TryAdd(key, value))
            {
                _entries++;
                _bytes += bytes;
            }
        }
    }
}
