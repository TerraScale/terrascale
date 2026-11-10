using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update;
using TerraScale.DotNet.TerraBase;

namespace TerraScale.EntityFrameworkCore.Update;

internal sealed class TerraBaseModificationCommandBatch(ModificationCommandBatchFactoryDependencies dependencies, int maxBatchSize)
    : AffectedCountModificationCommandBatch(dependencies, maxBatchSize)
{
    private const string RowConflict = "row_conflict";
    private const int MaxRequestBytes = 1024 * 1024;
    private const int MaxStatementBytes = 16 * 1024;
    private const int RequestBytes = 32;
    private const int StatementBytes = 32;
    private const int ScalarBytes = 64;
    private readonly List<Command> commands = [];
    private int statements;
    private long bytes = RequestBytes;
    private int unguarded;

    public override bool RequiresTransaction => unguarded > 0 && ModificationCommands.Count > 1;

    public override void Execute(IRelationalConnection connection) =>
        throw new NotSupportedException("TerraBase is async-only; use SaveChangesAsync.");

    public override async Task ExecuteAsync(IRelationalConnection connection, CancellationToken cancellationToken = default)
    {
        try
        {
            await base.ExecuteAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException exception) when (exception is not DbUpdateConcurrencyException
            && exception.InnerException is TerraBaseSqlException { Code: RowConflict } error
            && commands.FindIndex(command => command.Guarded && command.FirstStatement + 1 == error.Statement) is var index and >= 0)
        {
            throw new DbUpdateConcurrencyException(
                "The database operation was expected to affect 1 row(s), but actually affected 0 row(s); data may have been modified or deleted since entities were loaded.",
                exception.InnerException,
                ModificationCommands[index].Entries);
        }
    }

    protected override void AddCommand(IReadOnlyModificationCommand modificationCommand)
    {
        var start = SqlBuilder.Length;
        base.AddCommand(modificationCommand);
        var sql = SqlBuilder.ToString(start, SqlBuilder.Length - start);
        var written = modificationCommand.EntityState is EntityState.Modified or EntityState.Deleted;
        var reads = modificationCommand.ColumnModifications.Any(column => column.IsRead);
        var statement = written && !reads ? sql[..sql.LastIndexOf(TerraBaseUpdateSqlGenerator.RowsAffectedGuard, StringComparison.Ordinal)] : sql;
        if (Encoding.UTF8.GetByteCount(statement) > MaxStatementBytes)
        {
            throw new NotSupportedException(
                $"Saving a row of '{modificationCommand.TableName}' takes a statement longer than TerraBase's 16 KiB statement limit; split the table or its JSON columns.");
        }

        var command = new Command(statements, written, reads, Size(sql, modificationCommand));
        commands.Add(command);
        statements += command.Statements;
        bytes += command.Bytes;
        unguarded += command.Unguarded ? 1 : 0;
    }

    protected override void RollbackLastCommand(IReadOnlyModificationCommand modificationCommand)
    {
        var command = commands[^1];
        commands.RemoveAt(commands.Count - 1);
        statements -= command.Statements;
        bytes -= command.Bytes;
        unguarded -= command.Unguarded ? 1 : 0;
        base.RollbackLastCommand(modificationCommand);
    }

    protected override bool IsValid() => statements <= TerraBaseSqlStatement.MaxPerRequest && bytes <= MaxRequestBytes;

    private static long Size(string sql, IReadOnlyModificationCommand command)
    {
        var size = JsonSize(sql) + (long)StatementBytes * 2;
        foreach (var column in command.ColumnModifications)
        {
            if (column.UseCurrentValueParameter) size += ParameterSize(column, column.ParameterName, column.Value);
            if (column.UseOriginalValueParameter) size += ParameterSize(column, column.OriginalParameterName, column.OriginalValue);
        }

        return size;
    }

    private static long ParameterSize(IColumnModification column, string? name, object? value)
    {
        var stored = value is not null && column.TypeMapping?.Converter is { } converter ? converter.ConvertToProvider(value) : value;
        return (name?.Length ?? 0) + 4 + stored switch
        {
            null => 4,
            string text => JsonSize(text),
            char character => JsonSize(character.ToString()),
            byte[] blob => ((blob.Length + 2) / 3 * 4) + 14,
            _ => ScalarBytes,
        };
    }

    private static long JsonSize(string text)
    {
        long size = Encoding.UTF8.GetByteCount(text) + 2;
        foreach (var character in text)
        {
            size += character switch
            {
                '"' or '\\' => 1,
                < ' ' or (>= '\u007f' and <= '\u009f') or '\u2028' or '\u2029' => 5,
                _ => 0,
            };
        }

        return size;
    }

    private sealed record Command(int FirstStatement, bool Written, bool Reads, long Bytes)
    {
        public int Statements => Written ? 2 : 1;

        public bool Guarded => Written && !Reads;

        public bool Unguarded => Written && Reads;
    }
}

internal sealed class TerraBaseModificationCommandBatchFactory(ModificationCommandBatchFactoryDependencies dependencies, IDbContextOptions options)
    : IModificationCommandBatchFactory
{
    private readonly int maxBatchSize = Math.Min(RelationalOptionsExtension.Extract(options).MaxBatchSize ?? TerraBaseSqlStatement.MaxPerRequest, TerraBaseSqlStatement.MaxPerRequest);

    public ModificationCommandBatch Create() => new TerraBaseModificationCommandBatch(dependencies, maxBatchSize);
}
