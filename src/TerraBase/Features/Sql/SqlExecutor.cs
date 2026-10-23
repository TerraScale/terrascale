using System.Globalization;
using System.Text.Json;
using TerraBase.Engine;
using TerraBase.Errors;
using TerraBase.Features.Documents;
using TerraBase.Primitives;

namespace TerraBase.Features.Sql;

/// <summary>
/// Generated integer keys of one transaction, in order, so a replay of its statements generates the same keys
/// (docs/terrabase/sql/relational-sql.md section 5).
/// </summary>
internal sealed class SqlKeys
{
    private readonly List<long> _keys = [];
    private int _next;

    /// <summary>Starts a replay: the next keys are the logged ones, then new ones.</summary>
    public void Rewind() => _next = 0;

    /// <summary>The logged key at this position, or a new one from <paramref name="generate"/>.</summary>
    public long Next(Func<long> generate)
    {
        if (_next == _keys.Count)
        {
            _keys.Add(generate());
        }

        return _keys[_next++];
    }
}

/// <summary>The net document bytes a transaction's writes added, measured when the storage quota needs it.</summary>
/// <param name="Measure">Whether to measure.</param>
internal sealed class SqlTally(bool Measure)
{
    public bool Measure { get; } = Measure;

    public long Delta { get; set; }
}

/// <summary>Runs one statement in a storage transaction the caller commits; writes go through the document service.</summary>
internal sealed class SqlExecutor(IDocumentService documents, TimeProvider time)
{
    private static readonly SqlResult Empty = new([], [], 0, null);
    private static readonly DateTimeOffset KeyEpoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public async ValueTask<SqlOutcome> ExecuteAsync(
        IStorageTransaction transaction, ParsedSql statement, SqlCommand command, SqlKeys keys, SqlTally tally, bool describe,
        CancellationToken cancellationToken) =>
        statement switch
        {
            ReadSql read => await ReadAsync(transaction, read, command, describe, null, cancellationToken).ConfigureAwait(false),
            InsertSql insert => await InsertAsync(transaction, insert, command, keys, tally, cancellationToken).ConfigureAwait(false),
            ChangeSql change => await ChangeAsync(transaction, change, command, tally, cancellationToken).ConfigureAwait(false),
            CreateTableSql create => await CreateAsync(transaction, create, cancellationToken).ConfigureAwait(false),
            DropTableSql drop => await DropAsync(transaction, drop, cancellationToken).ConfigureAwait(false),
            _ => SqlErrors.Error("this statement is not supported here"),
        };

    private async ValueTask<SqlOutcome> CreateAsync(IStorageTransaction transaction, CreateTableSql create, CancellationToken cancellationToken)
    {
        if (await transaction.GetCollectionAsync(create.Name, cancellationToken).ConfigureAwait(false) is not null)
        {
            return create.IfNotExists && await SqlTable.ReadAsync(transaction, create.Name, cancellationToken).ConfigureAwait(false) is not null
                ? Empty
                : SqlErrors.Error("already exists: a table or a document collection has this name");
        }

        var collection = new CollectionInfo(create.Name, time.GetUtcNow().ToUnixTimeMilliseconds(), EntityIds.New(time).ToString());
        await transaction.CreateCollectionAsync(collection, cancellationToken).ConfigureAwait(false);
        await SqlTable.WriteAsync(transaction, create.Name, create.Table, cancellationToken).ConfigureAwait(false);
        if (create.Unique.Count > 0)
        {
            await transaction.SetUniqueConstraintsAsync(create.Name, create.Unique, cancellationToken).ConfigureAwait(false);
        }

        return Empty;
    }

    private static async ValueTask<SqlOutcome> DropAsync(IStorageTransaction transaction, DropTableSql drop, CancellationToken cancellationToken)
    {
        if (await SqlTable.ReadAsync(transaction, drop.Name, cancellationToken).ConfigureAwait(false) is null)
        {
            return drop.IfExists ? Empty : SqlErrors.Error("no such table");
        }

        await transaction.DropCollectionAsync(drop.Name, cancellationToken).ConfigureAwait(false);
        return Empty;
    }

    // The engine read of `read`: its tables' definitions, its parameters bound, its rows converted. `keyed` names the
    // table whose rows also expose their document id (`_id`), for UPDATE and DELETE.
    private static async ValueTask<SqlOutcome> ReadAsync(
        IStorageTransaction transaction, ReadSql read, SqlCommand command, bool describe, string? keyed, CancellationToken cancellationToken)
    {
        var tables = new Dictionary<string, SqlTableDefinition>(StringComparer.Ordinal);
        foreach (var name in read.Tables)
        {
            if (await SqlTable.ReadAsync(transaction, name, cancellationToken).ConfigureAwait(false) is not { } table)
            {
                return SqlErrors.Error("no such table");
            }

            tables[name] = table;
        }

        if (Bind(read.Params, command) is not { } parameters)
        {
            return SqlErrors.Error(string.Create(CultureInfo.InvariantCulture, $"the statement takes {read.Params.Count} arguments"));
        }

        SqlProgram program;
        try
        {
            program = SqlRead.Program(read, tables, parameters, describe, keyed);
        }
        catch (FormatException error)
        {
            // The row guard needs a column of every joined item.
            return SqlErrors.Error(error.Message);
        }

        var result = await transaction.ExecuteSqlAsync(program, cancellationToken).ConfigureAwait(false);
        if (result.Truncated)
        {
            return SqlErrors.TooBig("a result holds at most 10000 rows and 8 MiB; add a LIMIT");
        }

        var rows = new List<IReadOnlyList<SqlValue>>(result.Rows.Count);
        foreach (var row in result.Rows)
        {
            var values = new SqlValue[row.Length];
            for (var i = 0; i < row.Length; i++)
            {
                switch (row[i])
                {
                    case long integer:
                        values[i] = SqlValue.Of(integer);
                        break;
                    case double real:
                        values[i] = SqlValue.Of(real);
                        break;
                    case string text:
                        values[i] = SqlValue.Of(text);
                        break;
                    case null:
                        break;
                    default:
                        return SqlErrors.Error("blob values are not supported");
                }
            }

            rows.Add(values);
        }

        var types = SqlRead.DeclTypes(read, tables, result.Columns.Count);
        return new SqlResult([.. result.Columns.Select((name, i) => new SqlColumn(name, types[i]))], rows, 0, null);
    }

    /// <summary>
    /// One value per parameter: positional arguments by number, a named argument (with or without its prefix) over it;
    /// unbound parameters are NULL, as in SQLite. Null when more positional arguments than parameters arrive.
    /// </summary>
    private static object?[]? Bind(IReadOnlyList<string?> names, SqlCommand command)
    {
        if (command.Args.Count > names.Count)
        {
            return null;
        }

        var values = new object?[names.Count];
        for (var i = 0; i < names.Count; i++)
        {
            var value = i < command.Args.Count ? command.Args[i] : SqlValue.Null;
            if (names[i] is { } name && (command.Named.TryGetValue(name, out var named) || command.Named.TryGetValue(name[1..], out named)))
            {
                value = named;
            }

            values[i] = value.Kind switch
            {
                SqlValueKind.Integer => value.Integer,
                SqlValueKind.Real => value.Real,
                SqlValueKind.Text => value.Text,
                _ => null,
            };
        }

        return values;
    }

    private async ValueTask<SqlOutcome> InsertAsync(
        IStorageTransaction transaction, InsertSql insert, SqlCommand command, SqlKeys keys, SqlTally tally, CancellationToken cancellationToken)
    {
        if (await SqlTable.ReadAsync(transaction, insert.Table, cancellationToken).ConfigureAwait(false) is not { } table)
        {
            return SqlErrors.Error("no such table");
        }

        var targets = new List<SqlColumnDefinition>();
        foreach (var name in insert.Columns ?? [.. table.Columns.Select(column => column.Name)])
        {
            if (table.Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } column
                || targets.Contains(column))
            {
                return SqlErrors.Error("INSERT names each column of the table at most once");
            }

            targets.Add(column);
        }

        IReadOnlyList<IReadOnlyList<SqlValue>> rows = [[]];
        if (insert.Source is { } source)
        {
            switch (await ReadAsync(transaction, source with { Params = insert.Params }, command, false, null, cancellationToken).ConfigureAwait(false))
            {
                case SqlError error:
                    return error;
                case SqlResult result:
                    rows = result.Rows;
                    break;
            }
        }

        if (rows.Count > SqlWrite.MaxRows)
        {
            return SqlErrors.TooBig("a statement writes at most 1024 rows");
        }

        var integerKey = table.PrimaryKey is [var only] && Column(table, only).Affinity == SqlAffinity.Integer ? only : null;
        long affected = 0;
        long? lastRowid = null;
        foreach (var row in rows)
        {
            if (row.Count != targets.Count)
            {
                return SqlErrors.Error(string.Create(CultureInfo.InvariantCulture, $"the rows have {row.Count} values for {targets.Count} columns"));
            }

            var values = new Dictionary<string, SqlValue>(StringComparer.Ordinal);
            foreach (var column in table.Columns)
            {
                var index = targets.IndexOf(column);
                var value = index >= 0 ? row[index] : column.Default is { } fallback ? SqlWrite.Default(fallback) : SqlValue.Null;
                values[column.Name] = SqlWrite.Apply(column.Affinity, value);
            }

            if (integerKey is not null)
            {
                if (values[integerKey].Kind == SqlValueKind.Null)
                {
                    values[integerKey] = SqlValue.Of(await GenerateAsync(transaction, insert.Table, keys, cancellationToken).ConfigureAwait(false));
                }

                if (values[integerKey].Kind != SqlValueKind.Integer)
                {
                    return new SqlError("SQLITE_MISMATCH", "an INTEGER PRIMARY KEY holds integers only");
                }
            }

            if (table.Columns.FirstOrDefault(column => column.NotNull && values[column.Name].Kind == SqlValueKind.Null) is not null)
            {
                return SqlErrors.Constraint("NOTNULL", "a NOT NULL column has no value");
            }

            if (Key(table, values) is not { } id)
            {
                return new SqlError("SQLITE_MISMATCH", "a primary key is an integer or text, and a key at most 256 characters without control characters");
            }

            var existing = await transaction.GetAsync(insert.Table, id, cancellationToken).ConfigureAwait(false) is not null;
            if (existing && insert.Mode == InsertMode.Plain)
            {
                return SqlErrors.Constraint("PRIMARYKEY", "a row with this primary key exists");
            }

            if (existing && insert.Mode == InsertMode.Ignore)
            {
                continue;
            }

            var put = new PutOperation(insert.Table, id, Document(values), insert.Mode == InsertMode.Replace ? new Precondition(null, false, false) : Precondition.Absent);
            if (await ApplyAsync(transaction, put, id, tally, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }

            affected++;
            lastRowid = integerKey is null ? null : values[integerKey].Integer;
        }

        return new SqlResult([], [], affected, lastRowid);
    }

    private async ValueTask<SqlOutcome> ChangeAsync(
        IStorageTransaction transaction, ChangeSql change, SqlCommand command, SqlTally tally, CancellationToken cancellationToken)
    {
        if (await SqlTable.ReadAsync(transaction, change.Table, cancellationToken).ConfigureAwait(false) is not { } table)
        {
            return SqlErrors.Error("no such table");
        }

        var columns = new List<SqlColumnDefinition>();
        foreach (var (name, _, _) in change.Assignments)
        {
            if (table.Columns.FirstOrDefault(column => column.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) is not { } column || columns.Contains(column))
            {
                return SqlErrors.Error("SET assigns each column of the table at most once");
            }

            if (table.PrimaryKey.Contains(column.Name))
            {
                return SqlErrors.Error("a PRIMARY KEY column cannot be updated: delete the row and insert it again");
            }

            columns.Add(column);
        }

        var read = await ReadAsync(transaction, change.Rows with { Params = change.Params }, command, false, change.Table, cancellationToken).ConfigureAwait(false);
        if (read is not SqlResult { Rows: var rows })
        {
            return read;
        }

        if (rows.Count > SqlWrite.MaxRows)
        {
            return SqlErrors.TooBig("a statement writes at most 1024 rows");
        }

        foreach (var row in rows)
        {
            var id = row[0].Text!;
            DocumentOperation operation;
            if (change.IsDelete)
            {
                operation = new DeleteOperation(change.Table, id, new Precondition(null, false, false));
            }
            else
            {
                var ops = new List<PatchOpRequest>();
                var value = 1;
                for (var i = 0; i < columns.Count; i++)
                {
                    var (_, op, by) = change.Assignments[i];
                    var path = "/" + columns[i].Name;
                    var set = op == "set" ? SqlWrite.Apply(columns[i].Affinity, row[value++]) : SqlValue.Null;
                    if (op == "inc")
                    {
                        ops.Add(new PatchOpRequest("inc", path, default, SqlWrite.Json(SqlValue.Of(by))));
                    }
                    else if (set.Kind == SqlValueKind.Null)
                    {
                        if (columns[i].NotNull)
                        {
                            return SqlErrors.Constraint("NOTNULL", "a NOT NULL column has no value");
                        }

                        ops.Add(new PatchOpRequest("unset", path, default, default));
                    }
                    else
                    {
                        ops.Add(new PatchOpRequest("set", path, SqlWrite.Json(set), default));
                    }
                }

                if (DocumentPatch.CheckOps(ops) is not DocumentPatch patch)
                {
                    return SqlErrors.Error("the new values do not form a valid change");
                }

                operation = new PatchOperation(change.Table, id, patch, new Precondition(null, false, false));
            }

            if (await ApplyAsync(transaction, operation, id, tally, cancellationToken).ConfigureAwait(false) is { } refused)
            {
                return refused;
            }
        }

        return new SqlResult([], [], rows.Count, null);
    }

    // The document service's refusal of one operation, in SQLite's vocabulary, or null when it applied.
    private async ValueTask<SqlError?> ApplyAsync(
        IStorageTransaction transaction, DocumentOperation operation, string id, SqlTally tally, CancellationToken cancellationToken)
    {
        var before = tally.Measure ? await transaction.GetDocumentBytesAsync(operation.Collection, id, cancellationToken).ConfigureAwait(false) : 0;
        var refused = Refusal(await documents.ApplyAsync(transaction, operation, cancellationToken).ConfigureAwait(false));
        if (refused is null && tally.Measure)
        {
            tally.Delta += await transaction.GetDocumentBytesAsync(operation.Collection, id, cancellationToken).ConfigureAwait(false) - before;
        }

        return refused;
    }

    private static SqlError? Refusal(ApplyOutcome outcome) =>
        outcome switch
        {
            OperationResult => null,
            DocumentRejected { Error: var error } => error switch
            {
                Conflict { Code: "unique_violation" } => SqlErrors.Constraint("UNIQUE", "a UNIQUE column or columns already hold this value"),
                Validation { Code: "schema_validation_failed" } => SqlErrors.Constraint("CHECK", "the row does not satisfy the collection schema"),
                PreconditionFailed => SqlErrors.Constraint("PRIMARYKEY", "a row with this primary key exists"),
                PayloadTooLarge or Unavailable { Code: "capacity" } => SqlErrors.TooBig("a transaction writes at most 1024 rows and 2 MiB"),
                _ => SqlErrors.Error("the write was refused"),
            },
        };

    // A new INTEGER PRIMARY KEY below 2^53: milliseconds since 2026-01-01 times 4096 plus 12 random bits, retried on a
    // local collision. Logged in `keys`, so a replayed transaction reuses it.
    private async ValueTask<long> GenerateAsync(IStorageTransaction transaction, string table, SqlKeys keys, CancellationToken cancellationToken)
    {
        for (var attempt = 0; ; attempt++)
        {
            var key = keys.Next(() => ((long)(time.GetUtcNow() - KeyEpoch).TotalMilliseconds * 4096) + Random.Shared.Next(4096));
            if (attempt == 7 || await transaction.GetAsync(table, key.ToString(CultureInfo.InvariantCulture), cancellationToken).ConfigureAwait(false) is null)
            {
                return key;
            }
        }
    }

    private static SqlColumnDefinition Column(SqlTableDefinition table, string name) => table.Columns.First(column => column.Name == name);

    // The document id: a single key's canonical integer or text, a composite key's JSON array, or a new ULID.
    private static string? Key(SqlTableDefinition table, Dictionary<string, SqlValue> values)
    {
        string id;
        if (table.PrimaryKey.Count == 0)
        {
            id = EntityIds.New().ToString();
        }
        else if (table.PrimaryKey is [var single])
        {
            var value = values[single];
            id = value.Kind switch
            {
                SqlValueKind.Integer => value.Integer.ToString(CultureInfo.InvariantCulture),
                SqlValueKind.Text => value.Text!,
                _ => "",
            };
        }
        else
        {
            if (table.PrimaryKey.Any(column => values[column].Kind == SqlValueKind.Real))
            {
                return null;
            }

            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer))
            {
                writer.WriteStartArray();
                foreach (var column in table.PrimaryKey)
                {
                    SqlWrite.Write(writer, values[column]);
                }

                writer.WriteEndArray();
            }

            id = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
        }

        return id is { Length: > 0 and <= 256 } && !id.Any(char.IsControl) ? id : null;
    }

    // Every column with a value; SQL NULL is a missing field.
    private static byte[] Document(Dictionary<string, SqlValue> values)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var (name, value) in values.Where(pair => pair.Value.Kind != SqlValueKind.Null))
            {
                writer.WritePropertyName(name);
                SqlWrite.Write(writer, value);
            }

            writer.WriteEndObject();
        }

        return buffer.ToArray();
    }
}
