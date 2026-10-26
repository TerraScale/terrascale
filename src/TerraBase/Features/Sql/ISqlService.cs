using TerraBase.Tenancy;

namespace TerraBase.Features.Sql;

/// <summary>The storage class of a SQL value; blobs are refused because a JSON document cannot hold bytes.</summary>
internal enum SqlValueKind
{
    /// <summary>SQL <c>NULL</c>; a missing field.</summary>
    Null,

    /// <summary>A 64-bit integer.</summary>
    Integer,

    /// <summary>A double.</summary>
    Real,

    /// <summary>UTF-8 text.</summary>
    Text,
}

/// <summary>One SQL value, as bound to a parameter or read from a row.</summary>
/// <param name="Kind">Storage class.</param>
/// <param name="Integer">The value when <paramref name="Kind"/> is <see cref="SqlValueKind.Integer"/>.</param>
/// <param name="Real">The value when <paramref name="Kind"/> is <see cref="SqlValueKind.Real"/>.</param>
/// <param name="Text">The value when <paramref name="Kind"/> is <see cref="SqlValueKind.Text"/>.</param>
internal readonly record struct SqlValue(SqlValueKind Kind, long Integer = 0, double Real = 0, string? Text = null)
{
    /// <summary>SQL <c>NULL</c>.</summary>
    public static SqlValue Null => default;

    /// <summary>An integer.</summary>
    public static SqlValue Of(long value) => new(SqlValueKind.Integer, Integer: value);

    /// <summary>A real.</summary>
    public static SqlValue Of(double value) => new(SqlValueKind.Real, Real: value);

    /// <summary>Text.</summary>
    public static SqlValue Of(string value) => new(SqlValueKind.Text, Text: value);
}

/// <summary>One statement with its arguments.</summary>
/// <param name="Sql">The statement text, at most 16 KiB.</param>
/// <param name="Args">Positional arguments for <c>?</c> and <c>?NNN</c>.</param>
/// <param name="Named">Named arguments for <c>:name</c>, <c>@name</c> and <c>$name</c>, keyed without the prefix.</param>
internal sealed record SqlCommand(string Sql, IReadOnlyList<SqlValue> Args, IReadOnlyDictionary<string, SqlValue> Named);

/// <summary>A result column.</summary>
/// <param name="Name">Column name as SQLite reports it.</param>
/// <param name="DeclType">Declared type of a table column, or <see langword="null"/> for an expression.</param>
internal sealed record SqlColumn(string Name, string? DeclType);

/// <summary>What a statement returned.</summary>
/// <param name="Columns">Result columns; empty for statements without rows.</param>
/// <param name="Rows">Rows in order, each with one value per column.</param>
/// <param name="RowsAffected">Rows a write inserted, updated or deleted; 0 for reads and DDL.</param>
/// <param name="LastInsertRowid">The integer primary key of the last inserted row, when the table has one.</param>
internal sealed record SqlResult(IReadOnlyList<SqlColumn> Columns, IReadOnlyList<IReadOnlyList<SqlValue>> Rows, long RowsAffected, long? LastInsertRowid)
{
    /// <summary>Finalized once the protocol accepts these rows; discarded results incur no read charge.</summary>
    internal SqlReadCharge? ReadCharge { get; init; }
}

/// <summary>A read charge shared by protocol projections of a result; accepting it twice cannot double bill.</summary>
internal sealed class SqlReadCharge(Action accept)
{
    private Action? _accept = accept;

    public void Accept() => Interlocked.Exchange(ref _accept, null)?.Invoke();
}

/// <summary>Why a statement failed, in SQLite's vocabulary so libSQL and PostgreSQL clients can map it.</summary>
/// <param name="Code">
/// <c>SQLITE_ERROR</c> (syntax or unsupported), <c>SQLITE_CONSTRAINT_PRIMARYKEY</c>, <c>SQLITE_CONSTRAINT_UNIQUE</c>,
/// <c>SQLITE_CONSTRAINT_NOTNULL</c>, <c>SQLITE_AUTH</c> (scope), <c>SQLITE_BUSY</c> (transaction conflict or busy
/// database), <c>SQLITE_TOOBIG</c> (a limit), <c>SQLITE_INTERRUPT</c> (work budget or timeout).
/// </param>
/// <param name="Message">Human-readable reason; never quotes client text.</param>
internal sealed record SqlError(string Code, string Message)
{
    /// <summary>
    /// A stable sub-code for front ends that map errors (added by SQ1-e2): <c>no_such_table</c>, <c>no_such_column</c>,
    /// <c>already_exists</c>, <c>syntax</c>, <c>invalid_input</c>, <c>constraint</c>, <c>conflict</c>, <c>auth</c>,
    /// <c>too_big</c> or <c>interrupt</c>. The message prefixes it reads (<c>no such table</c>, <c>no such column</c>,
    /// <c>already exists</c>, <c>SQL syntax error</c>) are stable too.
    /// </summary>
    public string Kind => Code switch
    {
        "SQLITE_AUTH" => "auth",
        "SQLITE_BUSY" => "conflict",
        "SQLITE_TOOBIG" => "too_big",
        "SQLITE_INTERRUPT" => "interrupt",
        _ when Code.StartsWith("SQLITE_CONSTRAINT", StringComparison.Ordinal) => "constraint",
        _ when Message.StartsWith("no such table", StringComparison.Ordinal) => "no_such_table",
        _ when Message.StartsWith("no such column", StringComparison.Ordinal) => "no_such_column",
        _ when Message.StartsWith("already exists", StringComparison.Ordinal) => "already_exists",
        _ when Message.StartsWith("SQL syntax error", StringComparison.Ordinal) => "syntax",
        _ => "invalid_input",
    };
}

/// <summary>What a statement would take and return, without running its writes.</summary>
/// <param name="Params">One entry per parameter: its name with prefix, or <see langword="null"/> for <c>?</c>.</param>
/// <param name="Columns">Result columns.</param>
/// <param name="IsExplain">Whether the statement is <c>EXPLAIN</c>.</param>
/// <param name="IsReadOnly">Whether the statement only reads.</param>
internal sealed record SqlDescription(IReadOnlyList<string?> Params, IReadOnlyList<SqlColumn> Columns, bool IsExplain, bool IsReadOnly);

/// <summary>A statement's rows, or why it failed.</summary>
internal readonly union SqlOutcome(SqlResult, SqlError);

/// <summary>A statement's description, or why it cannot run.</summary>
internal readonly union SqlDescribeOutcome(SqlDescription, SqlError);

/// <summary>
/// A SQL connection to one database: autocommit, or inside a transaction that may span requests. Not thread-safe:
/// one request drives it at a time.
/// </summary>
internal interface ISqlSession : IAsyncDisposable
{
    /// <summary>Whether no transaction is open.</summary>
    bool IsAutocommit { get; }

    /// <summary>
    /// Runs one statement; <c>BEGIN</c>, <c>COMMIT</c> and <c>ROLLBACK</c> change the session.
    /// The adapter accepts a result's <see cref="SqlResult.ReadCharge"/> only at its protocol success boundary.
    /// </summary>
    ValueTask<SqlOutcome> ExecuteAsync(SqlCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Runs every statement of <paramref name="sql"/> in order without arguments, stopping at the first error; returns
    /// the last result (Hrana <c>sequence</c>). Added by SQ1-e; existing members are unchanged.
    /// </summary>
    ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, CancellationToken cancellationToken);

    /// <summary>
    /// <see cref="ExecuteScriptAsync(string, CancellationToken)"/> refusing with <c>SQLITE_TOOBIG</c>, before running anything,
    /// a script of more than <paramref name="maxStatements"/> statements. A script's statements share one 5 s deadline
    /// (added by SQ1-e9).
    /// </summary>
    ValueTask<SqlOutcome> ExecuteScriptAsync(string sql, int maxStatements, CancellationToken cancellationToken);

    /// <summary>Describes one statement.</summary>
    ValueTask<SqlDescribeOutcome> DescribeAsync(SqlCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Ends the current request: releases the storage transaction it held. An open SQL transaction survives in the
    /// session's replay log; the next <see cref="ExecuteAsync"/> resumes it.
    /// </summary>
    ValueTask EndRequestAsync();
}

/// <summary>A column of a SQL table, as <see cref="ISqlService.ListTablesAsync"/> reports it.</summary>
/// <param name="Name">Column name.</param>
/// <param name="DeclType">Declared type, or <see langword="null"/> when none was declared.</param>
/// <param name="NotNull">Whether the column is <c>NOT NULL</c> (primary key columns always are).</param>
/// <param name="PrimaryKey">Whether the column is part of the primary key.</param>
/// <param name="Default">The literal <c>DEFAULT</c> as SQL (<c>'x'</c>, <c>-1.5</c>), or <see langword="null"/> (added by SQ1-e10).</param>
internal sealed record SqlTableColumn(string Name, string? DeclType, bool NotNull, bool PrimaryKey, string? Default = null);

/// <summary>A SQL table of a database.</summary>
/// <param name="Name">Table name.</param>
/// <param name="Columns">Columns in declaration order.</param>
internal sealed record SqlTableInfo(string Name, IReadOnlyList<SqlTableColumn> Columns);

/// <summary>Runs relational SQL over a database's collections; shared by the HTTP SQL API, Hrana and PostgreSQL wire.</summary>
internal interface ISqlService
{
    /// <summary>
    /// Lists the SQL tables of <paramref name="database"/> in name order, at most 10,000 (added by SQ1-e2); empty when the
    /// database does not exist.
    /// </summary>
    ValueTask<IReadOnlyList<SqlTableInfo>> ListTablesAsync(TenantContext database, CancellationToken cancellationToken);

    /// <summary>Opens a session; writes and DDL fail with <c>SQLITE_AUTH</c> unless <paramref name="canWrite"/>.</summary>
    ISqlSession Open(TenantContext database, bool canWrite);
}
