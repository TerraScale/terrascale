using System.Globalization;
using Turso.Data.Sqlite;

namespace TerraBase.Engine.Turso;

internal static class StorageMigrations
{
    internal sealed record Sql(int From, int To, string Released, string Statement);

    private static readonly Sql[] Steps = [new(23, 24, "2026-11-12", "SELECT 1;")];

    internal static int FileFormat(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return StorageFormat.OfFile(Convert.ToInt32(command.ExecuteScalar(), CultureInfo.InvariantCulture));
    }

    internal static int Apply(string name, SqliteConnection connection, CancellationToken cancellationToken)
    {
        var format = FileFormat(connection);
        if (!StorageFormat.IsSupported(format))
        {
            throw new InvalidOperationException(StorageFormat.Refusal($"database '{name}'", format));
        }

        if (format >= StorageFormat.Target)
        {
            return format;
        }

        using var settings = connection.CreateCommand();
        settings.CommandText = "PRAGMA synchronous;";
        var synchronous = Convert.ToInt32(settings.ExecuteScalar(), CultureInfo.InvariantCulture);
        try
        {
            settings.CommandText = "PRAGMA synchronous = FULL;";
            settings.ExecuteNonQuery();
            for (var from = format; from < StorageFormat.Target; from++)
            {
                var step = Steps.Single(step => step.From == from);
                cancellationToken.ThrowIfCancellationRequested();
                using var transaction = connection.BeginTransaction(deferred: false);
                using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = step.Statement + $" PRAGMA user_version = {StorageFormat.FileVersion(step.To)};";
                command.ExecuteNonQuery();
                cancellationToken.ThrowIfCancellationRequested();
                transaction.Commit();
            }
        }
        finally
        {
            settings.CommandText = $"PRAGMA synchronous = {synchronous};";
            settings.ExecuteNonQuery();
        }

        return FileFormat(connection) == StorageFormat.Target
            ? StorageFormat.Target
            : throw new InvalidDataException($"database '{name}' did not reach storage format {StorageFormat.Id}");
    }
}
