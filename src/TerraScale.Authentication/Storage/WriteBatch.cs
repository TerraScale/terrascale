using System.Data.Common;
using System.Globalization;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.Authentication.Storage;

internal sealed record GuardedCommand(DbCommand Command, IReadOnlySet<int> Guards)
{
    public bool GuardFailed(Exception failure) =>
        failure is TerraBaseSqlException { Code: TerraBaseSqlErrorCodes.NotNullViolation } refused && Guards.Contains(refused.Statement);
}

internal sealed class WriteBatch
{
    public const int MaxStatements = 256;

    private const string Guard = "INSERT INTO write_guards (id, expected) SELECT 0, NULL WHERE changes() <> 1";

    private readonly List<(string Sql, bool Guarded)> units = [];
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
        units.Add((sql, guarded));
        statements += guarded ? 2 : 1;
    }

    public IEnumerable<GuardedCommand> Commands(DbConnection connection, DbTransaction? transaction)
    {
        ArgumentNullException.ThrowIfNull(connection);
        List<string> chunk = [];
        HashSet<int> guards = [];
        foreach (var (sql, guarded) in units)
        {
            if (chunk.Count + (guarded ? 2 : 1) > MaxStatements)
            {
                yield return new(Command(connection, transaction, chunk), guards);
                chunk = [];
                guards = [];
            }

            chunk.Add(sql);
            if (guarded)
            {
                guards.Add(chunk.Count);
                chunk.Add(Guard);
            }
        }

        if (chunk.Count > 0)
        {
            yield return new(Command(connection, transaction, chunk), guards);
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
