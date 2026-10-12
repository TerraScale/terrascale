using System.Reflection;
using System.Text;
using Turso.Data.Sqlite;
using Turso.Raw.Public;
using Turso.Raw.Public.Handles;
using Turso.Raw.Public.Value;

namespace TerraBase.Engine.Sqlite;

/// <summary>
/// Runs statements without result columns (writes, DDL, pragmas, transaction control) on a connection's native handle,
/// disposing every statement it prepares, failed or not. Turso.Data.Sqlite 0.8.2 (<c>SqliteCommand.Execute</c>) never
/// finalizes such a statement when it fails: the garbage collector's finalizer thread would finalize it later, a second
/// user of the connection that Turso refuses with "concurrent use forbidden" while the owner steps another statement.
/// Statements with result columns go through the provider's reader, which disposes them.
/// </summary>
internal static class TursoStatements
{
    private static readonly FieldInfo NativeHandle = typeof(SqliteConnection).GetField("_database", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingFieldException(nameof(SqliteConnection), "_database");

    // The provider's own conversion, so a parameter binds as it would through SqliteCommand.
    private static readonly Func<SqliteParameter, TursoValue> Value = (typeof(SqliteParameter).GetMethod("ToTursoValue", BindingFlags.NonPublic | BindingFlags.Instance)
        ?? throw new MissingMethodException(nameof(SqliteParameter), "ToTursoValue")).CreateDelegate<Func<SqliteParameter, TursoValue>>();

    /// <summary>Runs the statements of <paramref name="command"/> in order with its parameters; returns the rows they changed.</summary>
    public static int Run(this SqliteCommand command)
    {
        ArgumentNullException.ThrowIfNull(command);
        return Run(command.Connection ?? throw new InvalidOperationException("the command has no connection"), command.CommandText, command.Parameters);
    }

    /// <summary>Runs the statements of <paramref name="sql"/>, which take no parameters.</summary>
    public static int Run(this SqliteConnection connection, string sql) => Run(connection, sql, parameters: null);

    private static int Run(SqliteConnection connection, string sql, SqliteParameterCollection? parameters)
    {
        var native = NativeHandle.GetValue(connection) as TursoDatabaseHandle ?? throw new InvalidOperationException("the connection is closed");
        var changed = 0;
        foreach (var text in Split(sql))
        {
            try
            {
                using var statement = TursoBindings.PrepareStatement(native, text);
                Bind(statement, parameters);
                while (TursoBindings.Read(statement))
                {
                }

                changed += TursoBindings.RowsAffected(statement);
            }
            catch (TursoException error)
            {
                throw new SqliteException($"SQLite Error 1: '{error.Message}'.", 1);
            }
        }

        return changed;
    }

    // Every parameter the statement names, by name; a command may hold more, for its other statements.
    private static void Bind(TursoStatementHandle statement, SqliteParameterCollection? parameters)
    {
        for (var index = 1; index <= TursoBindings.GetParameterCount(statement); index++)
        {
            var name = TursoBindings.GetParameterName(statement, index);
            if (name is null || parameters is null || !parameters.Contains(name))
            {
                throw new InvalidOperationException($"the statement's parameter {name ?? index.ToString(System.Globalization.CultureInfo.InvariantCulture)} has no value");
            }

            TursoBindings.BindParameter(statement, index, Value(parameters[name]));
        }
    }

    /// <summary>The statements of <paramref name="sql"/>: split at semicolons outside quotes and line comments, as the provider does.</summary>
    internal static List<string> Split(string sql)
    {
        var statements = new List<string>();
        var current = new StringBuilder();
        var (single, dbl, comment) = (false, false, false);
        for (var i = 0; i < sql.Length; i++)
        {
            var c = sql[i];
            if (comment)
            {
                comment = c != '\n';
            }
            else if (!single && !dbl && c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                comment = true;
            }
            else if (c == '\'' && !dbl)
            {
                single = !single;
            }
            else if (c == '"' && !single)
            {
                dbl = !dbl;
            }
            else if (c == ';' && !single && !dbl)
            {
                Add(statements, current);
                continue;
            }

            current.Append(c);
        }

        Add(statements, current);
        return statements;
    }

    private static void Add(List<string> statements, StringBuilder current)
    {
        // A statement of comments only is no statement.
        if (current.ToString().Split('\n').Any(line => line.Trim() is { Length: > 0 } text && !text.StartsWith("--", StringComparison.Ordinal)))
        {
            statements.Add(current.ToString().Trim());
        }

        current.Clear();
    }
}
