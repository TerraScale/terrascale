using System.Data.Common;
using System.Globalization;

namespace TerraScale.Authentication.Storage;

internal sealed class WriteBatch
{
    public const int MaxStatements = 256;

    public const string GuardColumn = "write_guards.expected";

    private const string Guard = "INSERT INTO write_guards (id, expected) SELECT 0, NULL WHERE changes() <> 1";

    private readonly List<string> units = [];
    private readonly List<object?> arguments = [];
    private int statements;

    public int Statements => statements;

    public string Argument(object? value)
    {
        arguments.Add(value);
        return string.Create(CultureInfo.InvariantCulture, $"@p{arguments.Count - 1}");
    }

    public void Statement(string sql, bool guarded)
    {
        units.Add(guarded ? sql + ";\n" + Guard : sql);
        statements += guarded ? 2 : 1;
    }

    public IEnumerable<DbCommand> Commands(DbConnection connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        var chunk = new List<string>();
        var size = 0;
        foreach (var unit in units)
        {
            var count = unit.Contains(Guard, StringComparison.Ordinal) ? 2 : 1;
            if (size + count > MaxStatements)
            {
                yield return Command(connection, transaction, chunk);
                chunk = [];
                size = 0;
            }

            chunk.Add(unit);
            size += count;
        }

        if (chunk.Count > 0)
        {
            yield return Command(connection, transaction, chunk);
        }
    }

    private DbCommand Command(DbConnection connection, DbTransaction? transaction, IReadOnlyList<string> chunk)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = string.Join(";\n", chunk);
        for (var index = 0; index < arguments.Count; index++)
        {
            var parameter = command.CreateParameter();
            parameter.ParameterName = string.Create(CultureInfo.InvariantCulture, $"p{index}");
            parameter.Value = arguments[index];
            command.Parameters.Add(parameter);
        }

        return command;
    }
}
