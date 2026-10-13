namespace TerraBase.Features.Import;

/// <summary>The storage class of one source value.</summary>
internal enum SourceKind : byte
{
    Null,
    Integer,
    Real,
    Text,
    Blob,
}

/// <summary>
/// One source value. PostgreSQL values are always <see cref="SourceKind.Text"/> (the unescaped <c>COPY</c> text) or
/// <see cref="SourceKind.Null"/>; the column type maps them. SQLite values carry their storage class.
/// </summary>
internal readonly record struct SourceValue(SourceKind Kind, long Integer = 0, double Real = 0, string? Text = null, byte[]? Blob = null)
{
    public static SourceValue Null { get; } = new(SourceKind.Null);
}

/// <summary>A column as the source declares it.</summary>
internal sealed class SourceColumn
{
    public required string Name { get; init; }

    /// <summary>Declared type, lowercased and space-normalized; empty when SQLite declares none.</summary>
    public required string Type { get; init; }

    public bool NotNull { get; set; }

    /// <summary>A literal default, used for SQLite rows written before <c>ALTER TABLE … ADD COLUMN</c>.</summary>
    public SourceValue Default { get; set; } = SourceValue.Null;

    /// <summary>Whether a default exists that is not a literal.</summary>
    public bool ExpressionDefault { get; set; }

    /// <summary>A generated column: never imported (PostgreSQL omits it from <c>COPY</c>; SQLite VIRTUAL ones are not stored).</summary>
    public bool Generated { get; set; }

    /// <summary>A SQLite STORED generated column, which the record holds.</summary>
    public bool Stored { get; set; }
}

/// <summary>A table as the source declares it, before options and mapping apply.</summary>
internal sealed class SourceTable
{
    public required string Schema { get; init; }

    public required string Name { get; init; }

    public string Display => ImportNames.Table(Schema, Name);

    public List<SourceColumn> Columns { get; } = [];

    public List<string> PrimaryKey { get; set; } = [];

    public List<ImportConstraint> Constraints { get; } = [];

    /// <summary>Problems found while reading the definition (the table cannot be imported as declared).</summary>
    public List<ImportIssue> Issues { get; } = [];

    /// <summary>Rows in the export (PostgreSQL counts them while reading the schema; SQLite while mapping).</summary>
    public long Rows { get; set; }

    /// <summary>PostgreSQL: a partitioned parent, which holds no rows itself.</summary>
    public bool Partitioned { get; set; }

    /// <summary>PostgreSQL: parents named by <c>INHERITS</c> or <c>PARTITION OF</c>, whose columns this table also has.</summary>
    public List<List<string>> Parents { get; } = [];

    /// <summary>SQLite: the b-tree root page.</summary>
    public uint RootPage { get; init; }

    /// <summary>SQLite: <c>WITHOUT ROWID</c>.</summary>
    public bool WithoutRowid { get; init; }

    /// <summary>SQLite: the <c>INTEGER PRIMARY KEY</c> column that aliases the rowid.</summary>
    public string? RowidAlias { get; set; }

    public SourceColumn? Column(string name, bool ignoreCase) =>
        Columns.Find(column => string.Equals(column.Name, name, ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal));
}

/// <summary>What a source holds: its tables, what is not imported, and problems that block the whole source.</summary>
internal sealed class SourceSchema
{
    public const int MaxSkipped = 200;

    public List<SourceTable> Tables { get; } = [];

    public List<ImportSkipped> Skipped { get; } = [];

    public int SkippedTotal { get; private set; }

    /// <summary>Problems that belong to no table (an unknown statement, large objects); the first one rejects the source.</summary>
    public List<ImportIssue> Issues { get; } = [];

    /// <summary>Warnings that belong to no table.</summary>
    public List<ImportIssue> Warnings { get; } = [];

    public void Skip(string kind, string name)
    {
        SkippedTotal++;
        if (Skipped.Count < MaxSkipped)
        {
            Skipped.Add(new(kind, name));
        }
    }

    public void Block(string code, string message)
    {
        if (Issues.Count < 50)
        {
            Issues.Add(new("blocking", code, null, null, message));
        }
    }
}

/// <summary>One source row in read order.</summary>
/// <param name="Table">Its table.</param>
/// <param name="Values">Values in the order of <see cref="Columns"/>.</param>
/// <param name="Columns">The columns the values belong to (PostgreSQL: the <c>COPY</c> list; SQLite: every stored column).</param>
/// <param name="RowId">SQLite rowid, when the table has one.</param>
/// <param name="Resume">Where reading continues after this row.</param>
internal readonly record struct SourceRow(SourceTable Table, SourceValue[] Values, IReadOnlyList<SourceColumn> Columns, long? RowId, ImportPosition Resume);

/// <summary>
/// Where reading continues: for PostgreSQL the byte offset of the next line and, inside a <c>COPY</c> block, its table
/// and column list; for SQLite the table index and the rows already read from it.
/// </summary>
/// <param name="Offset">PostgreSQL byte offset.</param>
/// <param name="CopyTable">PostgreSQL table of the open <c>COPY</c> block.</param>
/// <param name="CopyColumns">Its column list.</param>
/// <param name="Table">SQLite table index.</param>
/// <param name="Row">SQLite rows already read from that table.</param>
internal sealed record ImportPosition(long Offset, string? CopyTable, IReadOnlyList<string>? CopyColumns, int Table, long Row);

/// <summary>A source that is malformed in a way that stops reading it.</summary>
internal sealed class SourceFormatException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

/// <summary>A readable source: its schema, and its rows from a position.</summary>
internal interface IImportSource : IDisposable
{
    SourceSchema Schema { get; }

    /// <summary>Rows in source order from <paramref name="from"/> (the start when null).</summary>
    IEnumerable<SourceRow> Rows(ImportPosition? from, CancellationToken cancellationToken);
}
