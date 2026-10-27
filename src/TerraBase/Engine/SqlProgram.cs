namespace TerraBase.Engine;

/// <summary>A relational table a <see cref="SqlProgram"/> reads: a collection whose documents are its rows.</summary>
/// <param name="Name">Collection name (validated like a database name).</param>
/// <param name="Columns">Column names, each a top-level field (a letter, then letters, digits or <c>_</c>).</param>
/// <param name="WithId">Whether the table also exposes each row's document id as column <c>_id</c>, for a write to address it.</param>
/// <param name="KeyColumn">
/// The column whose value is the document id itself (a single TEXT primary key). It is read from the id: a document write
/// drops a root <c>id</c> field equal to its key (docs/terrabase/id-contract.md).
/// </param>
/// <param name="KeyParameter">
/// With <paramref name="KeyColumn"/>, the number of the parameter the statement compares that column with: the engine reads
/// only the row whose document id equals it, through the id index. The statement keeps its own comparison.
/// </param>
public sealed record SqlProgramTable(string Name, IReadOnlyList<string> Columns, bool WithId = false, string? KeyColumn = null, int? KeyParameter = null);

/// <summary>
/// One validated relational statement. The engine exposes each of <paramref name="Tables"/> under its name, with one
/// column per field, ahead of <paramref name="Ctes"/>, and runs <paramref name="Statement"/> under the query work budget.
/// </summary>
/// <param name="Tables">Tables the statement reads.</param>
/// <param name="Ctes">The statement's own common table expressions (<c>name AS (…), …</c>), or <see langword="null"/>.</param>
/// <param name="Statement">The statement without its top-level <c>WITH</c>; parameters are named <c>$p1</c>, <c>$p2</c>, ….</param>
/// <param name="Parameters">One value per parameter, in order: <see langword="null"/>, <see cref="long"/>, <see cref="double"/> or <see cref="string"/>.</param>
/// <param name="MaxRows">Most rows to read.</param>
/// <param name="MaxBytes">Most bytes of values to read (8 per number).</param>
public sealed record SqlProgram(
    IReadOnlyList<SqlProgramTable> Tables, string? Ctes, string Statement, IReadOnlyList<object?> Parameters, int MaxRows, long MaxBytes)
{
    /// <summary>
    /// The function every SELECT of <see cref="Statement"/> calls in its WHERE, with an argument naming a column of each
    /// FROM item: the engine charges one unit of the work budget per call, so every evaluated row combination counts.
    /// </summary>
    public const string RowGuard = "tb_sql_row";

    /// <summary>
    /// The function a statement calls for SQL's <c>a || b</c>: SQLite's concatenation (NULL when either is NULL), but a
    /// result past <see cref="MaxStringBytes"/> throws <see cref="SqlValueTooLargeException"/>.
    /// </summary>
    public const string Concat = "tb_sql_concat";

    /// <summary>Largest string a relational read builds.</summary>
    public const int MaxStringBytes = 1024 * 1024;
}

/// <summary>What a <see cref="SqlProgram"/> returned.</summary>
/// <param name="Columns">Result column names.</param>
/// <param name="Rows">Rows: <see langword="null"/>, <see cref="long"/>, <see cref="double"/>, <see cref="string"/> or <see cref="byte"/> arrays.</param>
/// <param name="Truncated">Whether more rows or bytes followed than the program's bounds; <paramref name="Rows"/> stop there.</param>
public sealed record SqlProgramResult(IReadOnlyList<string> Columns, IReadOnlyList<object?[]> Rows, bool Truncated);

/// <summary>The engine refused or failed a <see cref="SqlProgram"/>; the message names the rule, never the statement's text.</summary>
public sealed class SqlProgramException : Exception
{
    /// <summary>Creates the failure.</summary>
    public SqlProgramException(string message, Exception inner) : base(message, inner) { }

    /// <inheritdoc />
    public SqlProgramException() { }

    /// <inheritdoc />
    public SqlProgramException(string message) : base(message) { }
}

/// <summary>A relational read built a string larger than <see cref="SqlProgram.MaxStringBytes"/>.</summary>
public sealed class SqlValueTooLargeException : Exception
{
    /// <inheritdoc />
    public SqlValueTooLargeException() : base("a string value is larger than 1 MiB") { }

    /// <inheritdoc />
    public SqlValueTooLargeException(string message) : base(message) { }

    /// <inheritdoc />
    public SqlValueTooLargeException(string message, Exception inner) : base(message, inner) { }
}
