using System.Text;

namespace TerraScale.Authentication.Storage;

public sealed class Table<T>
    where T : class
{
    private static Table<T>? registered;

    private readonly string[] columns;
    private readonly int[] keyOrdinals;
    private readonly Func<T, object?[]> values;
    private readonly Func<T, string?>? version;

    public Table(string name, IReadOnlyList<string> keys, IReadOnlyList<string> columns, Func<T, object?[]> values, Func<T, string?>? version)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(keys);
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentNullException.ThrowIfNull(values);
        Name = name;
        this.columns = [.. columns];
        keyOrdinals = [.. keys.Select(key => Array.IndexOf(this.columns, key))];
        if (keyOrdinals.Length == 0 || keyOrdinals.Any(ordinal => ordinal < 0))
        {
            throw new ArgumentException($"Every key of {name} must be one of its columns.", nameof(keys));
        }

        this.values = values;
        this.version = version;
        registered = this;
    }

    internal static Table<T> Of => AuthTables.Registered && registered is { } table
        ? table
        : throw new InvalidOperationException($"{typeof(T).Name} is not stored in a table.");

    public string Name { get; }

    public bool Versioned => version is not null;

    public string? VersionOf(T row) => version?.Invoke(row);

    internal string Identity(T row)
    {
        var current = values(row);
        var identity = new StringBuilder(Name);
        foreach (var ordinal in keyOrdinals)
        {
            identity.Append('\0').Append(current[ordinal]);
        }

        return identity.ToString();
    }

    internal void Insert(WriteBatch batch, T row, string? newVersion)
    {
        var current = values(row);
        var sql = new StringBuilder("INSERT INTO \"").Append(Name).Append("\" (");
        var arguments = new StringBuilder();
        for (var index = 0; index < columns.Length; index++)
        {
            sql.Append(index == 0 ? "\"" : ", \"").Append(columns[index]).Append('"');
            arguments.Append(index == 0 ? "" : ", ").Append(batch.Argument(current[index]));
        }

        if (Versioned)
        {
            sql.Append(", \"version\"");
            arguments.Append(", ").Append(batch.Argument(newVersion));
        }

        batch.Statement(sql.Append(") VALUES (").Append(arguments).Append(')').ToString(), guarded: false);
    }

    internal void Update(WriteBatch batch, T row, string expected, string newVersion)
    {
        var current = values(row);
        var sql = new StringBuilder("UPDATE \"").Append(Name).Append("\" SET ");
        var first = true;
        for (var index = 0; index < columns.Length; index++)
        {
            if (keyOrdinals.Contains(index))
            {
                continue;
            }

            sql.Append(first ? "\"" : ", \"").Append(columns[index]).Append("\" = ").Append(batch.Argument(current[index]));
            first = false;
        }

        sql.Append(first ? "\"version\" = " : ", \"version\" = ").Append(batch.Argument(newVersion));
        batch.Statement(Where(sql, batch, current, expected).ToString(), guarded: true);
    }

    internal void Delete(WriteBatch batch, T row, string? expected) =>
        batch.Statement(Where(new StringBuilder("DELETE FROM \"").Append(Name).Append('"'), batch, values(row), expected).ToString(), guarded: true);

    internal void Assert(WriteBatch batch, T row, string expected) =>
        batch.Statement(Where(new StringBuilder("UPDATE \"").Append(Name).Append("\" SET \"version\" = \"version\""), batch, values(row), expected).ToString(), guarded: true);

    private StringBuilder Where(StringBuilder sql, WriteBatch batch, object?[] current, string? expected)
    {
        sql.Append(" WHERE ");
        for (var index = 0; index < keyOrdinals.Length; index++)
        {
            var ordinal = keyOrdinals[index];
            sql.Append(index == 0 ? "\"" : " AND \"").Append(columns[ordinal]).Append("\" = ").Append(batch.Argument(current[ordinal]));
        }

        if (expected is not null)
        {
            sql.Append(" AND \"version\" = ").Append(batch.Argument(expected));
        }

        return sql;
    }
}
